using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Saves.Runs;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Simulation;

/// <summary>
/// A branch owns each materialized RNG. Fork shares only immutable captured values;
/// fingerprint and continuation readers do not materialize the nine native streams.
/// </summary>
internal sealed class CombatPredictionRngSet
{
    private sealed class FrozenStream(PredictionRngState state)
    {
        public readonly PredictionRngState State = state;
    }

    private struct Stream
    {
        private readonly FrozenStream? _frozen;
        private Rng? _mutable;

        private Stream(FrozenStream frozen) => _frozen = frozen;

        public Rng Mutable => _mutable ??= State.ToRng();
        public readonly PredictionRngState State => _mutable is { } rng
            ? rng.CaptureState() : _frozen!.State;

        // A caller can still hold a previously returned Rng and advance it after Fork.
        // Copy its complete state now; never share that mutable instance with a child.
        public readonly Stream Fork() => new(_mutable is null
            ? _frozen! : new FrozenStream(_mutable.CaptureState()));

        public static Stream Capture(Rng rng) => new(new FrozenStream(rng.CaptureState()));
    }

    private Stream _shuffle;
    public Rng Shuffle => _shuffle.Mutable;
    public PredictionRngState ShuffleState => _shuffle.State;

    private Stream _combatCardGeneration;
    public Rng CombatCardGeneration => _combatCardGeneration.Mutable;
    public PredictionRngState CombatCardGenerationState => _combatCardGeneration.State;

    private Stream _combatPotionGeneration;
    public Rng CombatPotionGeneration => _combatPotionGeneration.Mutable;
    public PredictionRngState CombatPotionGenerationState => _combatPotionGeneration.State;

    private Stream _combatCardSelection;
    public Rng CombatCardSelection => _combatCardSelection.Mutable;
    public PredictionRngState CombatCardSelectionState => _combatCardSelection.State;

    private Stream _combatEnergyCosts;
    public Rng CombatEnergyCosts => _combatEnergyCosts.Mutable;
    public PredictionRngState CombatEnergyCostsState => _combatEnergyCosts.State;

    private Stream _combatTargets;
    public Rng CombatTargets => _combatTargets.Mutable;
    public PredictionRngState CombatTargetsState => _combatTargets.State;

    private Stream _combatOrbGeneration;
    public Rng CombatOrbGeneration => _combatOrbGeneration.Mutable;
    public PredictionRngState CombatOrbGenerationState => _combatOrbGeneration.State;

    private Stream _monsterAi;
    public Rng MonsterAi => _monsterAi.Mutable;
    public PredictionRngState MonsterAiState => _monsterAi.State;

    private Stream _niche;
    public Rng Niche => _niche.Mutable;
    public PredictionRngState NicheState => _niche.State;

    private CombatPredictionRngSet(RunRngSet rng)
    {
        _shuffle = Stream.Capture(rng.Shuffle);
        _combatCardGeneration = Stream.Capture(rng.CombatCardGeneration);
        _combatPotionGeneration = Stream.Capture(rng.CombatPotionGeneration);
        _combatCardSelection = Stream.Capture(rng.CombatCardSelection);
        _combatEnergyCosts = Stream.Capture(rng.CombatEnergyCosts);
        _combatTargets = Stream.Capture(rng.CombatTargets);
        _combatOrbGeneration = Stream.Capture(rng.CombatOrbGeneration);
        _monsterAi = Stream.Capture(rng.MonsterAi);
        _niche = Stream.Capture(rng.Niche);
    }

    private CombatPredictionRngSet(
        Rng shuffle,
        Rng combatCardGeneration,
        Rng combatPotionGeneration,
        Rng combatCardSelection,
        Rng combatEnergyCosts,
        Rng combatTargets,
        Rng combatOrbGeneration,
        Rng monsterAi,
        Rng niche)
    {
        _shuffle = Stream.Capture(shuffle);
        _combatCardGeneration = Stream.Capture(combatCardGeneration);
        _combatPotionGeneration = Stream.Capture(combatPotionGeneration);
        _combatCardSelection = Stream.Capture(combatCardSelection);
        _combatEnergyCosts = Stream.Capture(combatEnergyCosts);
        _combatTargets = Stream.Capture(combatTargets);
        _combatOrbGeneration = Stream.Capture(combatOrbGeneration);
        _monsterAi = Stream.Capture(monsterAi);
        _niche = Stream.Capture(niche);
    }

    private CombatPredictionRngSet(CombatPredictionRngSet source)
    {
        _shuffle = source._shuffle.Fork();
        _combatCardGeneration = source._combatCardGeneration.Fork();
        _combatPotionGeneration = source._combatPotionGeneration.Fork();
        _combatCardSelection = source._combatCardSelection.Fork();
        _combatEnergyCosts = source._combatEnergyCosts.Fork();
        _combatTargets = source._combatTargets.Fork();
        _combatOrbGeneration = source._combatOrbGeneration.Fork();
        _monsterAi = source._monsterAi.Fork();
        _niche = source._niche.Fork();
    }

    public static CombatPredictionRngSet From(RunRngSet rng) => new(rng);

    /// <summary>
    /// Creates a combat-only RNG portfolio member without reading the live run RNG.
    /// Each combat stream receives an independent SplitMix64-derived seed.
    /// </summary>
    internal static CombatPredictionRngSet FromSampleSeed(ulong sampleSeed)
    {
        ulong state = sampleSeed;
        return new(
            new Rng(NextSampleSeed(ref state)),
            new Rng(NextSampleSeed(ref state)),
            new Rng(NextSampleSeed(ref state)),
            new Rng(NextSampleSeed(ref state)),
            new Rng(NextSampleSeed(ref state)),
            new Rng(NextSampleSeed(ref state)),
            new Rng(NextSampleSeed(ref state)),
            new Rng(NextSampleSeed(ref state)),
            new Rng(NextSampleSeed(ref state)));
    }

    private static ulong NextSampleSeed(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong value = state;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    internal SerializableRunRngSet ToSampleRunSnapshot(ulong sampleSeed)
    {
        SerializableRunRngSet result = new RunRngSet(
            "blind-" + sampleSeed.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToSerializable();
        result.Rngs[RunRngType.Shuffle] = ShuffleState.ToRng().ToSerializable();
        result.Rngs[RunRngType.CombatCardGeneration] = CombatCardGenerationState.ToRng().ToSerializable();
        result.Rngs[RunRngType.CombatPotionGeneration] = CombatPotionGenerationState.ToRng().ToSerializable();
        result.Rngs[RunRngType.CombatCardSelection] = CombatCardSelectionState.ToRng().ToSerializable();
        result.Rngs[RunRngType.CombatEnergyCosts] = CombatEnergyCostsState.ToRng().ToSerializable();
        result.Rngs[RunRngType.CombatTargets] = CombatTargetsState.ToRng().ToSerializable();
        result.Rngs[RunRngType.CombatOrbs] = CombatOrbGenerationState.ToRng().ToSerializable();
        result.Rngs[RunRngType.MonsterAi] = MonsterAiState.ToRng().ToSerializable();
        result.Rngs[RunRngType.Niche] = NicheState.ToRng().ToSerializable();
        return result;
    }

    internal CombatPredictionRngSet Fork() => new(this);
}
