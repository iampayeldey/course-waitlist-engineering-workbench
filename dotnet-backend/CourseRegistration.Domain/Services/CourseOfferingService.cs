using System.Collections.Concurrent;
using CourseRegistration.Domain.Models;

namespace CourseRegistration.Domain.Services;

/// <summary>
/// Business operations for a single course offering. Implementations must preserve
/// capacity, uniqueness, and waitlist ordering rules across each state transition.
/// </summary>
public interface ICourseOfferingService
{
    Task<EnrollmentResult> EnrollAsync(Learner learner);
    Task<DropResult> DropAsync(string learnerId);
    bool SatisfiesPrerequisites(Learner learner);
    InvariantCheckResult CheckInvariants();
    List<EnrolledRecord> GetEnrolled();
    List<WaitlistRecord> GetWaitlist();
    List<AuditLogEntry> GetAuditLogs();
    void Reset();
}

/// <summary>
/// In-memory implementation of enrollment, dropping, FIFO waitlist promotion, and auditing.
/// A semaphore serializes state-changing requests so concurrent calls cannot oversubscribe seats.
/// </summary>
public class CourseOfferingService : ICourseOfferingService
{
    private readonly CourseOfferingConfig _config;
    private readonly List<EnrolledRecord> _enrolled = new();
    private readonly List<WaitlistRecord> _waitlist = new();
    // Retains prerequisite history so eligibility can be checked again at promotion time.
    private readonly ConcurrentDictionary<string, Learner> _learnerRegistry = new();
    private readonly List<AuditLogEntry> _auditLogs = new();
    // Guards compound list updates; individual collection operations alone are not transactional.
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private int _sequenceCounter = 0;

    /// <summary>Creates a service for the supplied offering; capacity must be positive.</summary>
    public CourseOfferingService(CourseOfferingConfig config)
    {
        if (config.Capacity < 1)
        {
            throw new ArgumentException($"Capacity must be >= 1. Received: {config.Capacity}");
        }
        _config = config;
    }

    /// <summary>Returns whether the learner has completed every configured prerequisite.</summary>
    public bool SatisfiesPrerequisites(Learner learner)
    {
        if (_config.PrerequisiteCourseIds == null || _config.PrerequisiteCourseIds.Count == 0)
        {
            return true;
        }

        var completed = new HashSet<string>(learner.CompletedCourseIds ?? new List<string>());
        return _config.PrerequisiteCourseIds.All(req => completed.Contains(req));
    }

    /// <summary>
    /// Validates and enrolls a learner, or adds them to the FIFO waitlist when full.
    /// The semaphore keeps validation and mutation atomic relative to other requests.
    /// </summary>
    public async Task<EnrollmentResult> EnrollAsync(Learner learner)
    {
        await _semaphore.WaitAsync();
        try
        {
            // 1. Validation
            if (learner == null || string.IsNullOrWhiteSpace(learner.Id))
            {
                return new EnrollmentResult(false, EnrollmentOutcome.InvalidRequest, learner?.Id ?? "UNKNOWN", "Learner ID cannot be null or empty.");
            }

            var learnerId = learner.Id.Trim();
            var learnerName = string.IsNullOrWhiteSpace(learner.Name) ? learnerId : learner.Name.Trim();
            var sanitizedLearner = new Learner(learnerId, learnerName, learner.CompletedCourseIds ?? new List<string>());
            _learnerRegistry[learnerId] = sanitizedLearner;

            // 2. Duplicate Check: Enrolled
            if (_enrolled.Any(e => e.LearnerId == learnerId))
            {
                return new EnrollmentResult(false, EnrollmentOutcome.AlreadyEnrolled, learnerId, $"Learner {learnerId} is already actively enrolled.");
            }

            // 3. Duplicate Check: Waitlisted
            var waitlistIdx = _waitlist.FindIndex(w => w.LearnerId == learnerId);
            if (waitlistIdx != -1)
            {
                return new EnrollmentResult(false, EnrollmentOutcome.AlreadyWaitlisted, learnerId, $"Learner {learnerId} is already on waitlist at position #{waitlistIdx + 1}.", WaitlistPosition: waitlistIdx + 1);
            }

            // 4. Prerequisite Check
            if (!SatisfiesPrerequisites(sanitizedLearner))
            {
                var missing = _config.PrerequisiteCourseIds.Where(req => !sanitizedLearner.CompletedCourseIds.Contains(req));
                return new EnrollmentResult(false, EnrollmentOutcome.PrerequisiteNotMet, learnerId, $"Missing prerequisite courses: {string.Join(", ", missing)}.");
            }

            // 5. Available Seat Enrollment
            if (_enrolled.Count < _config.Capacity)
            {
                var seatNumber = _enrolled.Count + 1;
                var record = new EnrolledRecord(learnerId, learnerName, DateTime.UtcNow, seatNumber);
                _enrolled.Add(record);

                RecordAudit("ENROLL", "ENROLLED", learnerId, learnerName, $"Enrolled into seat #{seatNumber}.");
                AssertInvariants();

                return new EnrollmentResult(true, EnrollmentOutcome.Enrolled, learnerId, $"Successfully enrolled in {_config.CourseCode} (Seat #{seatNumber}).", SeatNumber: seatNumber);
            }

            // 6. Full Capacity: Queue into FIFO Waitlist
            _sequenceCounter++;
            var waitlistRecord = new WaitlistRecord(learnerId, learnerName, DateTime.UtcNow, _sequenceCounter);
            _waitlist.Add(waitlistRecord);
            var position = _waitlist.Count;

            RecordAudit("ENROLL", "WAITLISTED", learnerId, learnerName, $"Offering full ({_config.Capacity}/{_config.Capacity}). Added to waitlist at position #{position}.");
            AssertInvariants();

            return new EnrollmentResult(true, EnrollmentOutcome.Waitlisted, learnerId, $"Course offering full. Added to waitlist at position #{position}.", WaitlistPosition: position);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Removes an enrolled or waitlisted learner. Opening a seat triggers a FIFO scan
    /// that promotes the first eligible candidate under the configured ineligibility policy.
    /// </summary>
    public async Task<DropResult> DropAsync(string learnerIdInput)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(learnerIdInput))
            {
                return new DropResult(false, DropOutcome.InvalidRequest, learnerIdInput ?? "UNKNOWN", "Learner ID is required.");
            }

            var learnerId = learnerIdInput.Trim();

            // Case A: Dropping from Enrolled
            var enrolledIdx = _enrolled.FindIndex(e => e.LearnerId == learnerId);
            if (enrolledIdx != -1)
            {
                var dropped = _enrolled[enrolledIdx];
                _enrolled.RemoveAt(enrolledIdx);

                // Re-index seats
                for (int i = 0; i < _enrolled.Count; i++)
                {
                    _enrolled[i] = _enrolled[i] with { SeatNumber = i + 1 };
                }

                RecordAudit("DROP", "DROPPED_SEAT_OPENED", learnerId, dropped.LearnerName, $"Learner {learnerId} dropped from course offering.");

                // FIFO Waitlist Promotion with Skip-and-Retain
                var (promoted, skippedCount) = EvaluateAndPromoteNextEligible();
                AssertInvariants();

                if (promoted != null)
                {
                    return new DropResult(
                        true,
                        DropOutcome.DroppedAndPromoted,
                        learnerId,
                        $"Dropped {learnerId}. Seat #{promoted.SeatNumber} filled by promoting {promoted.LearnerName} from waitlist.",
                        PromotedLearnerId: promoted.LearnerId,
                        PromotedLearnerName: promoted.LearnerName,
                        SkippedIneligibleCount: skippedCount
                    );
                }

                return new DropResult(
                    true,
                    DropOutcome.DroppedSeatOpened,
                    learnerId,
                    $"Dropped {learnerId}. No eligible learners on waitlist; seat remains open."
                );
            }

            // Case B: Dropping from Waitlist
            var waitlistIdx = _waitlist.FindIndex(w => w.LearnerId == learnerId);
            if (waitlistIdx != -1)
            {
                var removed = _waitlist[waitlistIdx];
                _waitlist.RemoveAt(waitlistIdx);
                RecordAudit("DROP", "DROPPED_FROM_WAITLIST", learnerId, removed.LearnerName, $"Removed from waitlist position #{waitlistIdx + 1}.");
                AssertInvariants();

                return new DropResult(true, DropOutcome.DroppedFromWaitlist, learnerId, $"Learner {learnerId} removed from waitlist.");
            }

            // Case C: Learner neither enrolled nor waitlisted
            return new DropResult(false, DropOutcome.NotEnrolled, learnerId, $"Learner {learnerId} is not enrolled or waitlisted.");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Scans candidates in queue order. Under SkipAndRetain an ineligible candidate is
    /// skipped for this seat but stays in the queue; SkipAndEject removes that candidate.
    /// </summary>
    private (EnrolledRecord? Promoted, int SkippedCount) EvaluateAndPromoteNextEligible()
    {
        int skippedCount = 0;

        for (int i = 0; i < _waitlist.Count; i++)
        {
            var candidate = _waitlist[i];
            _learnerRegistry.TryGetValue(candidate.LearnerId, out var profile);
            profile ??= new Learner(candidate.LearnerId, candidate.LearnerName, new List<string>());

            if (SatisfiesPrerequisites(profile))
            {
                _waitlist.RemoveAt(i);
                var seatNumber = _enrolled.Count + 1;
                var newEnrolled = new EnrolledRecord(candidate.LearnerId, candidate.LearnerName, DateTime.UtcNow, seatNumber);
                _enrolled.Add(newEnrolled);

                RecordAudit("PROMOTE", "PROMOTED_FROM_WAITLIST", newEnrolled.LearnerId, newEnrolled.LearnerName, $"Promoted to seat #{seatNumber} (Skipped {skippedCount} preceding ineligible candidates).");
                return (newEnrolled, skippedCount);
            }
            else
            {
                skippedCount++;
                RecordAudit("REJECT", "PROMOTION_SKIPPED_INELIGIBLE", candidate.LearnerId, candidate.LearnerName, $"Candidate at position #{i + 1} skipped due to missing prerequisites.");

                if (_config.IneligiblePolicy == IneligiblePolicy.SkipAndEject)
                {
                    _waitlist.RemoveAt(i);
                    i--;
                }
            }
        }

        return (null, skippedCount);
    }

    /// <summary>Checks the offering's capacity, uniqueness, disjointness, and FIFO invariants.</summary>
    public InvariantCheckResult CheckInvariants()
    {
        var violations = new List<string>();

        // Invariant 1: 0 <= enrolled <= capacity
        if (_enrolled.Count > _config.Capacity)
        {
            violations.Add($"Capacity exceeded: {_enrolled.Count} > {_config.Capacity}");
        }

        // Invariant 2: Disjoint sets
        var enrolledSet = new HashSet<string>(_enrolled.Select(e => e.LearnerId));
        var waitlistSet = new HashSet<string>(_waitlist.Select(w => w.LearnerId));
        foreach (var id in enrolledSet)
        {
            if (waitlistSet.Contains(id))
            {
                violations.Add($"Disjointness violation: Learner {id} in both enrolled and waitlist.");
            }
        }

        // Invariant 3: Uniqueness in enrolled
        if (enrolledSet.Count != _enrolled.Count)
        {
            violations.Add("Duplicate entries found in enrolled records.");
        }

        // Invariant 4: Uniqueness in waitlist
        if (waitlistSet.Count != _waitlist.Count)
        {
            violations.Add("Duplicate entries found in waitlist records.");
        }

        // Invariant 5: Monotonic sequence numbers
        for (int i = 1; i < _waitlist.Count; i++)
        {
            if (_waitlist[i].SequenceNumber <= _waitlist[i - 1].SequenceNumber)
            {
                violations.Add($"Monotonicity violation at index {i}.");
            }
        }

        return new InvariantCheckResult(violations.Count == 0, violations, _enrolled.Count, _waitlist.Count, _config.Capacity);
    }

    /// <summary>Throws immediately if an operation leaves the offering in an invalid state.</summary>
    private void AssertInvariants()
    {
        var result = CheckInvariants();
        if (!result.Valid)
        {
            throw new InvalidOperationException($"Domain Invariant Violated: {string.Join("; ", result.Violations)}");
        }
    }

    /// <summary>Returns a copy of the enrolled roster so callers cannot mutate service state.</summary>
    public List<EnrolledRecord> GetEnrolled() => _enrolled.ToList();
    /// <summary>Returns a copy of the waitlist in FIFO order.</summary>
    public List<WaitlistRecord> GetWaitlist() => _waitlist.ToList();
    /// <summary>Returns a copy of the newest-first audit history.</summary>
    public List<AuditLogEntry> GetAuditLogs() => _auditLogs.ToList();

    /// <summary>Clears all runtime state while preserving the configured offering.</summary>
    public void Reset()
    {
        _enrolled.Clear();
        _waitlist.Clear();
        _auditLogs.Clear();
        _learnerRegistry.Clear();
        _sequenceCounter = 0;
    }

    /// <summary>Prepends an audit record with counts captured at the time of the event.</summary>
    private void RecordAudit(string action, string outcome, string learnerId, string learnerName, string details)
    {
        _auditLogs.Insert(0, new AuditLogEntry(
            Guid.NewGuid().ToString("N")[..8],
            DateTime.UtcNow,
            action,
            outcome,
            learnerId,
            learnerName,
            details,
            _enrolled.Count,
            _waitlist.Count,
            _config.Capacity
        ));
    }
}
