using Xunit;
using CourseRegistration.Domain.Models;
using CourseRegistration.Domain.Services;

namespace CourseRegistration.Tests;

/// <summary>
/// Regression tests for enrollment rules, FIFO promotion, prerequisite handling,
/// invalid requests, and concurrent access to the in-memory service.
/// </summary>
public class CourseOfferingServiceTests
{
    // Shared factory keeps each test's offering setup consistent while allowing focused overrides.
    private CourseOfferingConfig CreateConfig(int capacity = 2, List<string>? prereqs = null)
    {
        return new CourseOfferingConfig(
            "CS201-F26",
            "CS-201",
            "Data Structures",
            capacity,
            prereqs ?? new List<string> { "CS-101" }
        );
    }

    [Fact]
    public async Task EnrollAsync_WhenCapacityRemainsAndPrereqsMet_ShouldEnrollSuccessfully()
    {
        var service = new CourseOfferingService(CreateConfig(2));
        var learner = new Learner("L-101", "Payel Dey", new List<string> { "CS-101" });

        var result = await service.EnrollAsync(learner);

        Assert.True(result.Success);
        Assert.Equal(EnrollmentOutcome.Enrolled, result.Outcome);
        Assert.Equal(1, result.SeatNumber);
        Assert.Single(service.GetEnrolled());
        Assert.Empty(service.GetWaitlist());
        Assert.True(service.CheckInvariants().Valid);
    }

    [Fact]
    public async Task EnrollAsync_WhenCourseFull_ShouldJoinWaitlistWithSequentialPosition()
    {
        var service = new CourseOfferingService(CreateConfig(1));
        await service.EnrollAsync(new Learner("L-101", "Payel Dey", new List<string> { "CS-101" }));

        var res2 = await service.EnrollAsync(new Learner("L-102", "Bob Marley", new List<string> { "CS-101" }));
        var res3 = await service.EnrollAsync(new Learner("L-103", "Pallab Dey Parker", new List<string> { "CS-101" }));

        Assert.True(res2.Success);
        Assert.Equal(EnrollmentOutcome.Waitlisted, res2.Outcome);
        Assert.Equal(1, res2.WaitlistPosition);

        Assert.True(res3.Success);
        Assert.Equal(EnrollmentOutcome.Waitlisted, res3.Outcome);
        Assert.Equal(2, res3.WaitlistPosition);

        Assert.Single(service.GetEnrolled());
        Assert.Equal(2, service.GetWaitlist().Count);
        Assert.True(service.CheckInvariants().Valid);
    }

    [Fact]
    public async Task EnrollAsync_DuplicateRequests_ShouldBeIdempotentAndPreventDuplicates()
    {
        var service = new CourseOfferingService(CreateConfig(1));
        await service.EnrollAsync(new Learner("L-101", "Payel Dey", new List<string> { "CS-101" }));

        // Duplicate enrolled
        var dupEnrolled = await service.EnrollAsync(new Learner("L-101", "Payel Dey", new List<string> { "CS-101" }));
        Assert.False(dupEnrolled.Success);
        Assert.Equal(EnrollmentOutcome.AlreadyEnrolled, dupEnrolled.Outcome);

        // Bob Marley waitlisted
        await service.EnrollAsync(new Learner("L-102", "Bob Marley", new List<string> { "CS-101" }));

        // Duplicate waitlisted
        var dupWaitlist = await service.EnrollAsync(new Learner("L-102", "Bob Marley Duplicate", new List<string> { "CS-101" }));
        Assert.False(dupWaitlist.Success);
        Assert.Equal(EnrollmentOutcome.AlreadyWaitlisted, dupWaitlist.Outcome);
        Assert.Equal(1, dupWaitlist.WaitlistPosition);

        Assert.True(service.CheckInvariants().Valid);
    }

    [Fact]
    public async Task DropAsync_WhenEnrolledDrops_ShouldPromoteEarliestEligibleWaitlistedLearner()
    {
        var service = new CourseOfferingService(CreateConfig(1));
        await service.EnrollAsync(new Learner("L-101", "Payel Dey", new List<string> { "CS-101" }));
        await service.EnrollAsync(new Learner("L-102", "Bob Marley", new List<string> { "CS-101" }));
        await service.EnrollAsync(new Learner("L-103", "Pallab Dey Parker", new List<string> { "CS-101" }));

        var dropResult = await service.DropAsync("L-101");

        Assert.True(dropResult.Success);
        Assert.Equal(DropOutcome.DroppedAndPromoted, dropResult.Outcome);
        Assert.Equal("L-102", dropResult.PromotedLearnerId);

        var enrolled = service.GetEnrolled();
        Assert.Single(enrolled);
        Assert.Equal("L-102", enrolled[0].LearnerId);

        var waitlist = service.GetWaitlist();
        Assert.Single(waitlist);
        Assert.Equal("L-103", waitlist[0].LearnerId);
        Assert.True(service.CheckInvariants().Valid);
    }

    [Fact]
    public async Task EnrollAsync_WhenPrerequisiteMissing_ShouldRejectDirectEnrollment()
    {
        var service = new CourseOfferingService(CreateConfig(2, new List<string> { "CS-101", "MATH-150" }));
        var result = await service.EnrollAsync(new Learner("L-104", "Prof. Santa Ross", new List<string>()));

        Assert.False(result.Success);
        Assert.Equal(EnrollmentOutcome.PrerequisiteNotMet, result.Outcome);
        Assert.Empty(service.GetEnrolled());
        Assert.Empty(service.GetWaitlist());
        Assert.True(service.CheckInvariants().Valid);
    }

    [Fact]
    public async Task DropAsync_WhenWaitlistCandidateIneligible_ShouldSkipAndRetainPositionWhilePromotingNextEligible()
    {
        var service = new CourseOfferingService(CreateConfig(1));
        await service.EnrollAsync(new Learner("L-101", "Payel Dey", new List<string> { "CS-101" }));

        // Prof. Santa Ross lacks CS-101 (ineligible), Juin Kar has CS-101 (eligible)
        // Simulate Prof. Santa Ross on waitlist and Juin Kar behind him
        var serviceType = service.GetType();
        var waitlistField = serviceType.GetField("_waitlist", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var registryField = serviceType.GetField("_learnerRegistry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var registry = (System.Collections.Concurrent.ConcurrentDictionary<string, Learner>)registryField!.GetValue(service)!;
        var waitlist = (List<WaitlistRecord>)waitlistField!.GetValue(service)!;

        registry["L-104"] = new Learner("L-104", "Prof. Santa Ross", new List<string>());
        registry["L-105"] = new Learner("L-105", "Juin Kar", new List<string> { "CS-101" });

        waitlist.Add(new WaitlistRecord("L-104", "Prof. Santa Ross", DateTime.UtcNow, 1));
        waitlist.Add(new WaitlistRecord("L-105", "Juin Kar", DateTime.UtcNow, 2));

        var dropResult = await service.DropAsync("L-101");

        Assert.True(dropResult.Success);
        Assert.Equal(DropOutcome.DroppedAndPromoted, dropResult.Outcome);
        Assert.Equal("L-105", dropResult.PromotedLearnerId);
        Assert.Equal(1, dropResult.SkippedIneligibleCount);

        // Prof. Santa Ross is retained at head of waitlist
        Assert.Single(service.GetWaitlist());
        Assert.Equal("L-104", service.GetWaitlist()[0].LearnerId);
        Assert.True(service.CheckInvariants().Valid);
    }

    [Fact]
    public async Task DropAsync_InvalidAndDuplicateDrops_ShouldBeSafeAndPreserveState()
    {
        var service = new CourseOfferingService(CreateConfig(2));
        await service.EnrollAsync(new Learner("L-101", "Payel Dey", new List<string> { "CS-101" }));

        // Drop 1
        var drop1 = await service.DropAsync("L-101");
        Assert.True(drop1.Success);

        // Repeated drop on Payel Dey
        var drop2 = await service.DropAsync("L-101");
        Assert.False(drop2.Success);
        Assert.Equal(DropOutcome.NotEnrolled, drop2.Outcome);

        // Non-existent learner
        var dropGhost = await service.DropAsync("L-GHOST");
        Assert.False(dropGhost.Success);
        Assert.Equal(DropOutcome.NotEnrolled, dropGhost.Outcome);

        // Empty string
        var dropEmpty = await service.DropAsync("   ");
        Assert.False(dropEmpty.Success);
        Assert.Equal(DropOutcome.InvalidRequest, dropEmpty.Outcome);

        Assert.Empty(service.GetEnrolled());
        Assert.True(service.CheckInvariants().Valid);
    }

    [Fact]
    public async Task Concurrency_10SimultaneousEnrollments_ShouldEnforceAtomicCapacityBound()
    {
        var service = new CourseOfferingService(CreateConfig(2));

        // 10 concurrent requests
        var tasks = Enumerable.Range(1, 10).Select(i =>
            service.EnrollAsync(new Learner($"CONCURRENT-{i}", $"Racer {i}", new List<string> { "CS-101" }))
        ).ToList();

        var results = await Task.WhenAll(tasks);

        var enrolled = results.Count(r => r.Outcome == EnrollmentOutcome.Enrolled);
        var waitlisted = results.Count(r => r.Outcome == EnrollmentOutcome.Waitlisted);

        Assert.Equal(2, enrolled);
        Assert.Equal(8, waitlisted);
        Assert.Equal(2, service.GetEnrolled().Count);
        Assert.Equal(8, service.GetWaitlist().Count);
        Assert.True(service.CheckInvariants().Valid);
    }
}
