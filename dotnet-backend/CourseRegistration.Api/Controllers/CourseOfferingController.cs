using Microsoft.AspNetCore.Mvc;
using CourseRegistration.Domain.Models;
using CourseRegistration.Domain.Services;

namespace CourseRegistration.Api.Controllers;

/// <summary>
/// Controller for managing learner enrollment operations for a course offering.
/// This API exposes endpoints to enroll a learner, drop a learner, inspect status,
/// and reset the in-memory offering state.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class CourseOfferingController : ControllerBase
{
    private readonly ICourseOfferingService _service;

    /// <summary>
    /// Creates a new instance of the controller.
    /// Dependency injection supplies the business service that contains the enrollment logic.
    /// </summary>
    /// <param name="service">Course offering service implementing enrollment rules and state management.</param>
    public CourseOfferingController(ICourseOfferingService service)
    {
        _service = service;
    }

    /// <summary>
    /// Enrolls a learner in the course offering.
    /// </summary>
    /// <param name="learner">Learner information submitted in the request body.</param>
    /// <returns>
    /// Returns 200 OK with EnrollmentResult when the enrollment request is processed successfully.
    /// Returns 400 BadRequest when the request is invalid.
    /// </returns>
    [HttpPost("enroll")]
    public async Task<ActionResult<EnrollmentResult>> Enroll([FromBody] Learner learner)
    {
        var result = await _service.EnrollAsync(learner);

        // If the request is invalid, return a 400 response so the client can correct input.
        if (!result.Success && result.Outcome == EnrollmentOutcome.InvalidRequest)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Removes a learner from the course offering.
    /// </summary>
    /// <param name="learnerId">Unique identifier of the learner to drop.</param>
    /// <returns>
    /// Returns 200 OK with DropResult on success.
    /// Returns 400 BadRequest when the request is invalid.
    /// </returns>
    [HttpPost("drop/{learnerId}")]
    public async Task<ActionResult<DropResult>> Drop(string learnerId)
    {
        var result = await _service.DropAsync(learnerId);

        // Invalid learner ID or not allowed action should be returned as a client error.
        if (!result.Success && result.Outcome == DropOutcome.InvalidRequest)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Retrieves the current registration state of the offering.
    /// This includes enrolled students, waitlist, invariant validation results,
    /// and audit log entries for review/debugging.
    /// </summary>
    /// <returns>Current offering status and internal state snapshot.</returns>
    [HttpGet("status")]
    public ActionResult<object> GetStatus()
    {
        return Ok(new
        {
            Enrolled = _service.GetEnrolled(),
            Waitlist = _service.GetWaitlist(),
            Invariants = _service.CheckInvariants(),
            AuditLogs = _service.GetAuditLogs()
        });
    }

    /// <summary>
    /// Resets the in-memory course offering state to its initial condition.
    /// Useful during testing and demos when a fresh state is required.
    /// </summary>
    /// <returns>Confirmation message that the offering was reset.</returns>
    [HttpPost("reset")]
    public IActionResult Reset()
    {
        _service.Reset();
        return Ok(new { message = "Offering reset successfully." });
    }
}

// Developer notes:
// - Base route for this controller is: /api/CourseOffering
// - Example enroll endpoint: POST /api/CourseOffering/enroll
// - Example drop endpoint: POST /api/CourseOffering/drop/{learnerId}
// - Example status endpoint: GET /api/CourseOffering/status
// - Example reset endpoint: POST /api/CourseOffering/reset
// - This controller is intentionally simple and follows standard ASP.NET Core conventions.


















































