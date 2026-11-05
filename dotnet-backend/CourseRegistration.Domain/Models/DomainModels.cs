namespace CourseRegistration.Domain.Models;

/// <summary>Identity and prerequisite history supplied for an enrollment attempt.</summary>
public record Learner(
    string Id,
    string Name,
    List<string> CompletedCourseIds
);

/// <summary>Controls whether ineligible waitlist candidates remain queued or are removed.</summary>
public enum IneligiblePolicy
{
    SkipAndRetain,
    SkipAndEject
}

/// <summary>Immutable configuration describing one course offering and its eligibility rules.</summary>
public record CourseOfferingConfig(
    string Id,
    string CourseCode,
    string CourseTitle,
    int Capacity,
    List<string> PrerequisiteCourseIds,
    IneligiblePolicy IneligiblePolicy = IneligiblePolicy.SkipAndRetain
);

/// <summary>A learner's assigned seat and enrollment timestamp.</summary>
public record EnrolledRecord(
    string LearnerId,
    string LearnerName,
    DateTime EnrolledAt,
    int SeatNumber
);

/// <summary>A learner's FIFO queue entry, with a monotonically increasing sequence number.</summary>
public record WaitlistRecord(
    string LearnerId,
    string LearnerName,
    DateTime JoinedAt,
    int SequenceNumber
);

/// <summary>Classification of the result of an enrollment request.</summary>
public enum EnrollmentOutcome
{
    Enrolled,
    Waitlisted,
    AlreadyEnrolled,
    AlreadyWaitlisted,
    PrerequisiteNotMet,
    InvalidRequest
}

/// <summary>Outcome and user-facing details returned from an enrollment request.</summary>
public record EnrollmentResult(
    bool Success,
    EnrollmentOutcome Outcome,
    string LearnerId,
    string Message,
    int? SeatNumber = null,
    int? WaitlistPosition = null,
    long Timestamp = 0
);

/// <summary>Classification of the result of a drop request.</summary>
public enum DropOutcome
{
    DroppedSeatOpened,
    DroppedAndPromoted,
    DroppedFromWaitlist,
    NotEnrolled,
    InvalidRequest
}

/// <summary>Outcome of a drop, including any learner promoted to fill the vacated seat.</summary>
public record DropResult(
    bool Success,
    DropOutcome Outcome,
    string LearnerId,
    string Message,
    string? PromotedLearnerId = null,
    string? PromotedLearnerName = null,
    int SkippedIneligibleCount = 0,
    long Timestamp = 0
);

/// <summary>Result of checking capacity, uniqueness, disjointness, and FIFO invariants.</summary>
public record InvariantCheckResult(
    bool Valid,
    List<string> Violations,
    int EnrolledCount,
    int WaitlistCount,
    int Capacity
);

/// <summary>Audit event captured when the offering changes or a promotion is evaluated.</summary>
public record AuditLogEntry(
    string Id,
    DateTime Timestamp,
    string Action,
    string Outcome,
    string LearnerId,
    string LearnerName,
    string Details,
    int EnrolledCount,
    int WaitlistCount,
    int Capacity
);
