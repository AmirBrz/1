using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Samian.Application.Contracts;

namespace Samian.WebApi.Controllers;

// The "observable pipeline" endpoint (brief line 39 + ROADMAP step 18): a reviewer can SEE the jobs
// move Pending → Processing → Done, and watch a poisoned job climb Attempts and land in Dead.
//
// GET /api/jobs?status=Dead IS the dead-letter queue view — no separate DLQ table to explain, because
// Dead is just a status on the same durable row (ADR-2). The tenant filter is applied inside the
// inspector, so one tenant never sees another's jobs or error text.
[ApiController]
[Route("api/jobs")]
[Authorize(Roles = "Admin")]   // pipeline health is an operator concern, not a shopper's
public sealed class JobsController : ControllerBase
{
    private readonly IPipelineInspector _pipeline;

    public JobsController(IPipelineInspector pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _pipeline = pipeline;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<JobResponse>>> List(
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        var jobs = await _pipeline.GetJobsAsync(status, cancellationToken);

        return Ok(jobs);
    }
}
