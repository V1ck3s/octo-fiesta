using Microsoft.AspNetCore.Mvc;

namespace octo_fiesta.Controllers;

/// <summary>
/// Admin API endpoints for managing provider instances.
/// </summary>
[ApiController]
[Route("admin/instances")]
public class AdminInstancesController : ControllerBase
{
    private readonly Services.InstanceManager _instanceManager;
    private readonly Services.InstanceRefreshHostedService _refreshService;
    private readonly ILogger<AdminInstancesController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminInstancesController"/> class.
    /// </summary>
    public AdminInstancesController(
        Services.InstanceManager instanceManager,
        Services.InstanceRefreshHostedService refreshService,
        ILogger<AdminInstancesController> logger)
    {
        _instanceManager = instanceManager;
        _refreshService = refreshService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current ordered list of instances for a given type.
    /// </summary>
    /// <param name="type">Instance type: "api" or "streaming".</param>
    /// <returns>List of instances with speed and error info.</returns>
    [HttpGet]
    public async Task<IActionResult> GetInstances([FromQuery] string type = "api")
    {
        if (!TryParseInstanceType(type, out var instanceType))
        {
            return BadRequest(new { error = "Invalid type. Use 'api' or 'streaming'." });
        }

        var instances = await _instanceManager.GetInstancesAsync(instanceType);
        return Ok(new
        {
            type = type.ToLowerInvariant(),
            instances = instances.Select((i, index) => new
            {
                index,
                baseUrl = i.BaseUrl,
                latencyMs = double.IsInfinity(i.LatencyMs) || i.LatencyMs == double.MaxValue
                    ? (double?)null
                    : Math.Round(i.LatencyMs, 2),
                error = i.Error,
                lastTested = i.LastTested
            })
        });
    }

    /// <summary>
    /// Reorders instances by moving one from a source index to a destination index.
    /// </summary>
    [HttpPost("reorder")]
    public async Task<IActionResult> ReorderInstances([FromBody] ReorderRequest request)
    {
        if (request == null)
        {
            return BadRequest(new { error = "Request body is required." });
        }

        if (!TryParseInstanceType(request.Type, out var instanceType))
        {
            return BadRequest(new { error = "Invalid type. Use 'api' or 'streaming'." });
        }

        try
        {
            await _instanceManager.ReorderInstancesAsync(instanceType, request.From, request.To);
            _logger.LogInformation("Reordered {Type} instances: moved {From} to {To}",
                request.Type, request.From, request.To);
            return Ok(new { success = true });
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Triggers an immediate refresh of all speed tests.
    /// </summary>
    [HttpPost("refresh")]
    public IActionResult RefreshInstances()
    {
        _refreshService.TriggerRefresh();
        _logger.LogInformation("Manual instance refresh triggered via API");
        return Ok(new { success = true, message = "Refresh triggered" });
    }

    private static bool TryParseInstanceType(string? typeString, out Services.InstanceType instanceType)
    {
        instanceType = Services.InstanceType.Api;

        if (string.IsNullOrWhiteSpace(typeString))
            return true;

        if (typeString.Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            instanceType = Services.InstanceType.Api;
            return true;
        }

        if (typeString.Equals("streaming", StringComparison.OrdinalIgnoreCase))
        {
            instanceType = Services.InstanceType.Streaming;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Request model for reordering instances.
    /// </summary>
    public class ReorderRequest
    {
        /// <summary>Instance type: "api" or "streaming".</summary>
        public string Type { get; set; } = "api";

        /// <summary>Source index to move from.</summary>
        public int From { get; set; }

        /// <summary>Destination index to move to.</summary>
        public int To { get; set; }
    }
}
