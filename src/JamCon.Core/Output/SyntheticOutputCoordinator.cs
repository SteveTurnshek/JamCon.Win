namespace JamCon.Core.Output;

/// <summary>
/// Owns every synthetic down event JamCon emits. Ported from SyntheticOutputCoordinator.swift.
/// </summary>
/// <remarks>
/// An output goes down for its first owner and stays down until its last owner releases it.
/// Duplicate reports from one owner are idempotent. Device teardown can release everything that
/// device holds even if its button state was lost or a physical release report never arrived,
/// which is what keeps a dropped Bluetooth link from leaving a mouse button stuck down.
/// </remarks>
public sealed class SyntheticOutputCoordinator(Action<SyntheticOutputEvent> post)
{
    private readonly Dictionary<SyntheticOutput, HashSet<SyntheticOutputOwner>> _ownersByOutput = new();
    private readonly Dictionary<SyntheticOutputOwner, HashSet<SyntheticOutput>> _outputsByOwner = new();

    public int ActiveOutputCount => _ownersByOutput.Count;

    public int OwnerCount(SyntheticOutput output) =>
        _ownersByOutput.TryGetValue(output, out var owners) ? owners.Count : 0;

    public void Set(SyntheticOutput output, bool pressed, SyntheticOutputOwner owner)
    {
        if (pressed) Press(output, owner);
        else Release(output, owner);
    }

    public void Tap(SyntheticOutput output, SyntheticOutputOwner owner)
    {
        // If another owner holds this output, pulse it up and down and leave it held. Reference
        // counting would otherwise swallow the tap and it would never reach Windows.
        if (_ownersByOutput.TryGetValue(output, out var owners) && owners.Count > 0)
        {
            post(new SyntheticOutputEvent(output, false));
            post(new SyntheticOutputEvent(output, true));
            return;
        }

        Press(output, owner);
        Release(output, owner);
    }

    public void ReleaseAll(string deviceId)
    {
        foreach (var owner in _outputsByOwner.Keys.Where(o => o.DeviceId == deviceId).ToArray())
        {
            ReleaseAll(owner);
        }
    }

    public void ReleaseAll(SyntheticOutputOwner owner)
    {
        if (!_outputsByOwner.TryGetValue(owner, out var outputs)) return;
        foreach (var output in outputs.ToArray()) Release(output, owner);
    }

    public void ReleaseAll()
    {
        var active = _ownersByOutput.Keys.ToArray();
        _ownersByOutput.Clear();
        _outputsByOwner.Clear();
        foreach (var output in active) post(new SyntheticOutputEvent(output, false));
    }

    private void Press(SyntheticOutput output, SyntheticOutputOwner owner)
    {
        if (!_ownersByOutput.TryGetValue(output, out var owners))
        {
            owners = [];
            _ownersByOutput[output] = owners;
        }

        if (!owners.Add(owner)) return;

        if (!_outputsByOwner.TryGetValue(owner, out var ownerOutputs))
        {
            ownerOutputs = [];
            _outputsByOwner[owner] = ownerOutputs;
        }

        ownerOutputs.Add(output);
        if (owners.Count == 1) post(new SyntheticOutputEvent(output, true));
    }

    private void Release(SyntheticOutput output, SyntheticOutputOwner owner)
    {
        if (!_ownersByOutput.TryGetValue(output, out var owners) || !owners.Remove(owner)) return;

        if (_outputsByOwner.TryGetValue(owner, out var ownerOutputs))
        {
            ownerOutputs.Remove(output);
            if (ownerOutputs.Count == 0) _outputsByOwner.Remove(owner);
        }

        if (owners.Count == 0)
        {
            _ownersByOutput.Remove(output);
            post(new SyntheticOutputEvent(output, false));
        }
    }
}
