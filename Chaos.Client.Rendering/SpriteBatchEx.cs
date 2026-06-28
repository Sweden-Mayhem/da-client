using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Chaos.Client.Rendering;

/// <summary>
///     An extended <see cref="SpriteBatch" /> type that allows reading of current state and stacked Begin/End calls
///     This allows you to Begin() additional SpriteBatchs while within existing, then End() to finalise the current
///     and resume the previous (without having to know the previous settings and reapply them manually)
///     It also allows to query the current batch render state
/// </summary>
public class SpriteBatchEx: SpriteBatch
{
    public struct BatchState
    {
        public SpriteSortMode SortMode;
        public BlendState? BlendState;
        public SamplerState? SamplerState;
        public DepthStencilState? DepthStencilState;
        public RasterizerState? RasterizerState;
        public Effect? Effect;
        public Matrix? TransformMatrix;
    }

    protected List<BatchState> State = [];

    public bool IsActive { get => State.Count > 0; }

    public int Depth { get => State.Count; }
    public SpriteSortMode SpriteSortMode { get => State[^1].SortMode; }
    public BlendState? BlendState { get => State[^1].BlendState; }
    public SamplerState? SamplerState { get => State[^1].SamplerState; }
    public DepthStencilState? DepthStencilState { get => State[^1].DepthStencilState; }
    public RasterizerState? RasterizerState { get => State[^1].RasterizerState; }
    public Effect? Effect { get => State[^1].Effect; }
    public Matrix? TransformMatrix { get => State[^1].TransformMatrix; }

    public SpriteBatchEx(GraphicsDevice graphicsDevice, int capacity = 0)
        : base(graphicsDevice, capacity)
    {
    }

    //
    // Summary:
    //     Begins a new SpriteBatch session with specified options
    //     If there was a previous SpriteBatch session that will be Flushed and then resumed again once End() is called
    public new void Begin(SpriteSortMode sortMode = SpriteSortMode.Deferred, BlendState? blendState = null, SamplerState? samplerState = null, DepthStencilState? depthStencilState = null, RasterizerState? rasterizerState = null, Effect? effect = null, Matrix? transformMatrix = null)
    {
        if (State.Count >= 100)
            throw new InvalidOperationException("Likely SpriteBatchEx leak - more than 100 nested batches.");

        if (State.Count > 0)
            base.End();

        State.Add(new BatchState
            {
                SortMode = sortMode,
                BlendState = blendState,
                SamplerState = samplerState,
                DepthStencilState = depthStencilState,
                RasterizerState = rasterizerState,
                Effect = effect,
                TransformMatrix = transformMatrix,
            }
        );

        base.Begin(sortMode, blendState, samplerState, depthStencilState, rasterizerState, effect, transformMatrix);
    }

    //
    // Summary:
    //     Flushes all batched text and sprites to the screen, and ends the current SpriteBatch
    //     If was a previous SpriteBatch that had been started with Begin(), this is resumed
    public new void End()
    {
        if (State.Count < 1)
            throw new InvalidOperationException("End without matching Begin.");

        base.End();

        State.RemoveAt(State.Count - 1);

        if (State.Count > 0)
        {
            var state = State[^1];
            base.Begin(state.SortMode, state.BlendState, state.SamplerState, state.DepthStencilState, state.RasterizerState, state.Effect, state.TransformMatrix);
        }
    }

    //
    // Summary:
    //     Flushes all batched text and sprites to the screen, but leaves the current batch still active
    //     (The equivalent to an End() and then a Begin() again with the same settings)
    public void Flush()
    {
        if (State.Count < 1)
            throw new InvalidOperationException("Flush without Begin.");

        var state = State[^1];

        base.End();
        base.Begin(state.SortMode, state.BlendState, state.SamplerState, state.DepthStencilState, state.RasterizerState, state.Effect, state.TransformMatrix);
    }

    //
    // Summary:
    //     Immediately releases the unmanaged resources used by this object.
    //
    // Parameters:
    //   disposing:
    //     true to release both managed and unmanaged resources; false to release only unmanaged
    //     resources.
    protected override void Dispose(bool disposing)
    {
        if (State.Count > 0)
            throw new InvalidOperationException("SpriteBatchEx is missing an End.");

        base.Dispose(disposing);
    }
}
