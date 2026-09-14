using Aprillz.MewUI;

using Clash.Config;
using Clash.Outbound;

namespace Clash.Gui;

internal sealed class NodeCardVm
{
    public NodeCardVm(ProxyNode node)
    {
        Node = node;
        Id = Guid.NewGuid();
    }

    public Guid Id { get; }
    public ProxyNode Node { get; }
    public string Name => Node.Name;
    public string Protocol =>
        string.IsNullOrEmpty(Node.ClientFingerprint) ? Node.Type : Node.Type + " (fp 未生效)";

    /// <summary>Plain integer ms or "-" / "…".</summary>
    public ObservableValue<string> LatencyText { get; } = new("-");

    public int? LatencyMs { get; private set; }
    public HealthStatus Status { get; private set; } = HealthStatus.Idle;

    public void SetChecking()
    {
        Status = HealthStatus.Checking;
        LatencyText.Value = "…";
    }

    public void Apply(HealthCheckResult result)
    {
        Status = result.Status;
        LatencyMs = result.LatencyMs;
        LatencyText.Value = result.LatencyMs is int ms ? ms.ToString() : "-";
    }

    public void SetCancelled()
    {
        if (Status == HealthStatus.Checking)
        {
            Status = HealthStatus.Idle;
            LatencyText.Value = LatencyMs is int ms ? ms.ToString() : "-";
        }
    }
}
