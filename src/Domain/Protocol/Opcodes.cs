// Port of packages/shared/src/protocol/opcodes.ts — keep in lockstep with the original.
//
// PARTIAL PORT: only `InstanceKind` is ported so far (needed by the client's dayNightCycle.ts). The rest of the
// module (ClientOpcode, ServerOpcode, EventKind, …) belongs to whoever ports the protocol; add it here in TS order.

namespace Fluitown.Domain;

/// <summary>The kind of live space a connection is attached to. The type `InstanceKind` is `int`.</summary>
public static class InstanceKind
{
    public const int Hub = 0;
    public const int Run = 1;
}
