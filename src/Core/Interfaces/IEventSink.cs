using fuseraft.Core.Models;

namespace fuseraft.Core.Interfaces;

/// <summary>
/// Typed event sink for structured execution events emitted during tool execution.
/// Distinct from <see cref="fuseraft.Orchestration.EventEmitter"/>, which is an untyped JSONL sink.
/// Implementations buffer events in memory; the projector drains them per turn.
/// </summary>
public interface IEventSink
{
    void Emit(ExecutionEvent evt);
}
