using Legio.Core;
using Legio.Core.Executio;

namespace Legio.Benchmark.Simulations;

/// <summary>
/// Realistic 12-Cohort AAA Game-Engine Frame Pipeline with Strong Resource Contention.
/// Exercises:
/// - 10 shared Vexillum resources producing all 3 hazard types (RAW, WAR, WAW)
/// - Deep 6-phase branched DAG constructed automatically by Legatus.ConstructConsilium()
///   Phase 0: SpatialHashRebuild + AnimationSkinning + AudioPropagation (3 parallel!)
///   Phase 1: AiPerception + PhysicsForce (2 parallel!)
///   Phase 2: CombatDamage + LocomotionSteering (2 parallel!)
///   Phase 3: HealthStatus + TransformIntegration (2 parallel!)
///   Phase 4: BossDirectorAi (micro-cohort 128!) + FrustumCullingLod (2 parallel!)
///   Phase 5: RenderPacketAssembly
/// - Mix of heavy skewed cohorts (32k entities) and micro-cohorts (128 &amp; 256 entities)
///   testing both Optio work stealing and ML ThreadCount scaling (down to 1 thread for micro tasks).
/// </summary>
public sealed class GameEngineDagSimulation
{
    // 10 shared engine resources mapped to Vexillum bits
    private const int ResTransform    = 0;
    private const int ResVelocity     = 1;
    private const int ResSpatialGrid  = 2;
    private const int ResAiBlackboard = 3;
    private const int ResAnimPose     = 4;
    private const int ResHealth       = 5;
    private const int ResDamageQueue  = 6;
    private const int ResAudioState   = 7;
    private const int ResVisibility   = 8;
    private const int ResRenderPacket = 9;

    private const float Dt = 0.016f;
    private const float WorldExtent = 256.0f;

    public readonly int EntityCount;
    public readonly int AiCount;
    public readonly int AudioSourceCount = 256;
    public readonly int BossCount = 128;

    // SoA engine component tables
    private readonly float[] _px, _py, _pz;
    private readonly float[] _vx, _vy, _vz;
    private readonly int[] _gridCell;
    private readonly int[] _aiTarget;
    private readonly float[] _aiThreat;
    private readonly float[] _animPhase, _animBone0, _animBone1, _animBone2;
    private readonly float[] _health, _armor;
    private readonly float[] _pendingDamage;
    private readonly float[] _audioGain;
    private readonly byte[] _lodTier;
    private readonly float[] _renderSortKey;
    private readonly int[] _combatHotspotWeight;

    private readonly PriorPilus[] _cohorts;

    public GameEngineDagSimulation(int entityCount = 32_768)
    {
        EntityCount = entityCount;
        AiCount = entityCount / 2;

        _px = new float[entityCount];
        _py = new float[entityCount];
        _pz = new float[entityCount];
        _vx = new float[entityCount];
        _vy = new float[entityCount];
        _vz = new float[entityCount];
        _gridCell = new int[entityCount];
        _aiTarget = new int[entityCount];
        _aiThreat = new float[entityCount];
        _animPhase = new float[entityCount];
        _animBone0 = new float[entityCount];
        _animBone1 = new float[entityCount];
        _animBone2 = new float[entityCount];
        _health = new float[entityCount];
        _armor = new float[entityCount];
        _pendingDamage = new float[entityCount];
        _audioGain = new float[AudioSourceCount];
        _lodTier = new byte[entityCount];
        _renderSortKey = new float[entityCount];
        _combatHotspotWeight = new int[entityCount];

        var rng = new Random(4242);
        for (int i = 0; i < entityCount; i++)
        {
            _px[i] = ((float)rng.NextDouble() * 2f - 1f) * WorldExtent;
            _py[i] = ((float)rng.NextDouble() * 2f - 1f) * 32f;
            _pz[i] = ((float)rng.NextDouble() * 2f - 1f) * WorldExtent;
            _vx[i] = ((float)rng.NextDouble() * 2f - 1f) * 8f;
            _vy[i] = ((float)rng.NextDouble() * 2f - 1f) * 1.5f;
            _vz[i] = ((float)rng.NextDouble() * 2f - 1f) * 8f;
            _animPhase[i] = (float)rng.NextDouble() * 6.283185f;
            _health[i] = 100.0f;
            _armor[i] = 0.1f + (float)rng.NextDouble() * 0.4f;

            // Spatial combat hotspot: 12% of entities are in intense melee brawl (16x heavier AI/Combat)
            bool inHotspot = (i >= entityCount / 8 && i < entityCount / 4);
            _combatHotspotWeight[i] = inHotspot ? 32 : 2;
        }

        _cohorts =
        [
            new DelegateCohort(0, () => EntityCount, StepSpatialHash),
            new DelegateCohort(1, () => AiCount, StepAnimationSkinning),
            new DelegateCohort(2, () => AudioSourceCount, StepAudioPropagation),
            new DelegateCohort(3, () => AiCount, StepAiPerception),
            new DelegateCohort(4, () => EntityCount, StepPhysicsForces),
            new DelegateCohort(5, () => AiCount, StepCombatDamage),
            new DelegateCohort(6, () => EntityCount, StepLocomotionSteering),
            new DelegateCohort(7, () => EntityCount, StepHealthAndStatus),
            new DelegateCohort(8, () => EntityCount, StepTransformIntegration),
            new DelegateCohort(9, () => BossCount, StepBossDirector),
            new DelegateCohort(10, () => EntityCount, StepFrustumCullingLod),
            new DelegateCohort(11, () => EntityCount, StepRenderPacketAssembly)
        ];
    }

    public void RegisterAll(Legatus legatus)
    {
        // C0 [Phase 0]: SpatialHashRebuild — R(Transform) -> W(SpatialGrid)
        var c0 = new Cohors(0, _cohorts[0]);
        c0.ReadMask.Tollere(ResTransform);
        c0.WriteMask.Tollere(ResSpatialGrid);

        // C1 [Phase 0]: AnimationSkinning — R(Velocity) -> W(AnimPose)
        var c1 = new Cohors(1, _cohorts[1]);
        c1.ReadMask.Tollere(ResVelocity);
        c1.WriteMask.Tollere(ResAnimPose);

        // C2 [Phase 0]: AudioPropagation (micro 256) — R(Transform, Velocity) -> W(AudioState)
        var c2 = new Cohors(2, _cohorts[2]);
        c2.ReadMask.Tollere(ResTransform);
        c2.ReadMask.Tollere(ResVelocity);
        c2.WriteMask.Tollere(ResAudioState);

        // C3 [Phase 1]: AiPerception — R(Transform, SpatialGrid, Health) -> W(AiBlackboard)
        var c3 = new Cohors(3, _cohorts[3]);
        c3.ReadMask.Tollere(ResTransform);
        c3.ReadMask.Tollere(ResSpatialGrid);
        c3.ReadMask.Tollere(ResHealth);
        c3.WriteMask.Tollere(ResAiBlackboard);

        // C4 [Phase 1]: PhysicsForces — R(Transform, SpatialGrid) -> W(Velocity) [WAR Velocity vs C1/C2!]
        var c4 = new Cohors(4, _cohorts[4]);
        c4.ReadMask.Tollere(ResTransform);
        c4.ReadMask.Tollere(ResSpatialGrid);
        c4.WriteMask.Tollere(ResVelocity);

        // C5 [Phase 2]: CombatDamage — R(AiBlackboard, AnimPose, SpatialGrid) -> W(DamageQueue)
        var c5 = new Cohors(5, _cohorts[5]);
        c5.ReadMask.Tollere(ResAiBlackboard);
        c5.ReadMask.Tollere(ResAnimPose);
        c5.ReadMask.Tollere(ResSpatialGrid);
        c5.WriteMask.Tollere(ResDamageQueue);

        // C6 [Phase 2]: LocomotionSteering — R(AiBlackboard, Transform) -> W(Velocity) [WAW Velocity vs C4!]
        var c6 = new Cohors(6, _cohorts[6]);
        c6.ReadMask.Tollere(ResAiBlackboard);
        c6.ReadMask.Tollere(ResTransform);
        c6.WriteMask.Tollere(ResVelocity);

        // C7 [Phase 3]: HealthAndStatus — R(DamageQueue) -> W(Health) [WAR Health vs C3!]
        var c7 = new Cohors(7, _cohorts[7]);
        c7.ReadMask.Tollere(ResDamageQueue);
        c7.WriteMask.Tollere(ResHealth);

        // C8 [Phase 3]: TransformIntegration — R(Velocity) -> W(Transform) [WAR Transform vs C0/C2/C3/C4/C6!]
        var c8 = new Cohors(8, _cohorts[8]);
        c8.ReadMask.Tollere(ResVelocity);
        c8.WriteMask.Tollere(ResTransform);

        // C9 [Phase 4]: BossDirectorAi (micro 128) — R(Health, Transform, AiBlackboard) -> W(AudioState) [WAW AudioState vs C2!]
        var c9 = new Cohors(9, _cohorts[9]);
        c9.ReadMask.Tollere(ResHealth);
        c9.ReadMask.Tollere(ResTransform);
        c9.ReadMask.Tollere(ResAiBlackboard);
        c9.WriteMask.Tollere(ResAudioState);

        // C10 [Phase 4]: FrustumCullingLod — R(Transform, AnimPose) -> W(Visibility)
        var c10 = new Cohors(10, _cohorts[10]);
        c10.ReadMask.Tollere(ResTransform);
        c10.ReadMask.Tollere(ResAnimPose);
        c10.WriteMask.Tollere(ResVisibility);

        // C11 [Phase 5]: RenderPacketAssembly — R(Transform, Visibility, Health, AnimPose) -> W(RenderPacket)
        var c11 = new Cohors(11, _cohorts[11]);
        c11.ReadMask.Tollere(ResTransform);
        c11.ReadMask.Tollere(ResVisibility);
        c11.ReadMask.Tollere(ResHealth);
        c11.ReadMask.Tollere(ResAnimPose);
        c11.WriteMask.Tollere(ResRenderPacket);

        legatus.RegisterCohors(c0);
        legatus.RegisterCohors(c1);
        legatus.RegisterCohors(c2);
        legatus.RegisterCohors(c3);
        legatus.RegisterCohors(c4);
        legatus.RegisterCohors(c5);
        legatus.RegisterCohors(c6);
        legatus.RegisterCohors(c7);
        legatus.RegisterCohors(c8);
        legatus.RegisterCohors(c9);
        legatus.RegisterCohors(c10);
        legatus.RegisterCohors(c11);

        legatus.ConstructConsilium();
    }

    // --- Phase 0 Kernels ---
    private void StepSpatialHash(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            int cx = (int)((_px[i] + WorldExtent) * 0.125f) & 63;
            int cz = (int)((_pz[i] + WorldExtent) * 0.125f) & 63;
            _gridCell[i] = (cx << 6) | cz;
        }
    }

    private void StepAnimationSkinning(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float speed = MathF.Sqrt(_vx[i] * _vx[i] + _vz[i] * _vz[i] + 0.01f);
            float phase = _animPhase[i] + speed * Dt * 1.4f;
            if (phase > 6.283185f) phase -= 6.283185f;
            _animPhase[i] = phase;

            // 3-bone hierarchical trig blend
            float s0 = MathF.Sin(phase);
            float c0 = MathF.Cos(phase);
            float s1 = MathF.Sin(phase * 2.0f + 0.5f);
            _animBone0[i] = s0 * 0.65f + c0 * 0.35f;
            _animBone1[i] = c0 * s1 * 0.8f;
            _animBone2[i] = MathF.Sin(phase * 0.5f + _animBone1[i]) * 0.5f;
        }
    }

    private void StepAudioPropagation(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            int e = (i * 97) % EntityCount;
            float distSq = _px[e] * _px[e] + _py[e] * _py[e] + _pz[e] * _pz[e] + 16.0f;
            float doppler = 1.0f + (_vx[e] * 0.003f);
            _audioGain[i] = (240.0f / distSq) * doppler;
        }
    }

    // --- Phase 1 Kernels ---
    private void StepAiPerception(int start, int length)
    {
        int end = start + length;
        int n = EntityCount;
        for (int i = start; i < end; i++)
        {
            float px = _px[i], pz = _pz[i];
            int myCell = _gridCell[i];
            int probes = _combatHotspotWeight[i];
            float bestThreat = -1.0f;
            int bestTarget = i;

            int baseIdx = (i * 17) % n;
            for (int k = 0; k < probes; k++)
            {
                int j = baseIdx + k;
                if (j >= n) j -= n;

                float dx = _px[j] - px;
                float dz = _pz[j] - pz;
                float dSq = dx * dx + dz * dz + 1.0f;
                float cellBonus = (_gridCell[j] == myCell) ? 2.5f : 1.0f;
                float threat = (_health[j] * cellBonus) / dSq;
                if (threat > bestThreat)
                {
                    bestThreat = threat;
                    bestTarget = j;
                }
            }

            _aiTarget[i] = bestTarget;
            _aiThreat[i] = bestThreat;
        }
    }

    private void StepPhysicsForces(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float drag = 0.985f;
            float cellWind = ((_gridCell[i] & 15) - 7.5f) * 0.12f;
            _vx[i] = _vx[i] * drag + cellWind * Dt;
            _vy[i] = _vy[i] * drag - 9.81f * Dt;
            _vz[i] = _vz[i] * drag - (_pz[i] * 0.002f) * Dt;
        }
    }

    // --- Phase 2 Kernels ---
    private void StepCombatDamage(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            int target = _aiTarget[i];
            float swing = MathF.Abs(_animBone0[i] + _animBone1[i]);
            int strikes = _combatHotspotWeight[i] >> 1;
            float dmg = 0.0f;

            for (int s = 0; s < strikes; s++)
            {
                float crit = MathF.Sin((i + s) * 0.37f + _animBone2[i]) > 0.7f ? 1.75f : 1.0f;
                float cellPenalty = (_gridCell[i] == _gridCell[target]) ? 1.0f : 0.35f;
                dmg += (0.4f + swing * 0.6f) * crit * cellPenalty * _aiThreat[i] * 0.05f;
            }

            _pendingDamage[i] = dmg;
        }
    }

    private void StepLocomotionSteering(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            int t = _aiTarget[i];
            float dx = _px[t] - _px[i];
            float dz = _pz[t] - _pz[i];
            float inv = 1.0f / MathF.Sqrt(dx * dx + dz * dz + 1.0f);
            _vx[i] += dx * inv * 1.5f * Dt;
            _vz[i] += dz * inv * 1.5f * Dt;
        }
    }

    // --- Phase 3 Kernels ---
    private void StepHealthAndStatus(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float dmg = _pendingDamage[i] * (1.0f - _armor[i]);
            float hp = _health[i] - dmg + 0.15f; // slight regen
            if (hp < 10.0f) hp = 100.0f;         // respawn for steady simulation
            else if (hp > 100.0f) hp = 100.0f;
            _health[i] = hp;
        }
    }

    private void StepTransformIntegration(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float px = _px[i] + _vx[i] * Dt;
            float py = _py[i] + _vy[i] * Dt;
            float pz = _pz[i] + _vz[i] * Dt;

            if (px > WorldExtent) px -= WorldExtent * 2f;
            else if (px < -WorldExtent) px += WorldExtent * 2f;
            if (pz > WorldExtent) pz -= WorldExtent * 2f;
            else if (pz < -WorldExtent) pz += WorldExtent * 2f;
            if (py < 0f) { py = 0f; _vy[i] = -_vy[i] * 0.4f; }

            _px[i] = px;
            _py[i] = py;
            _pz[i] = pz;
        }
    }

    // --- Phase 4 Kernels ---
    private void StepBossDirector(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            int e = (i * 131) % AiCount;
            float intensity = (100.0f - _health[e]) * 0.01f + _aiThreat[e] * 0.1f;
            _audioGain[i & (AudioSourceCount - 1)] += intensity * 0.05f;
        }
    }

    private void StepFrustumCullingLod(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            float px = _px[i], py = _py[i], pz = _pz[i];
            float distSq = px * px + py * py + pz * pz;
            bool inFrustum = (pz > -180.0f) && (MathF.Abs(px) < MathF.Abs(pz) * 1.4f + 60.0f);
            if (!inFrustum)
            {
                _lodTier[i] = 255; // culled
            }
            else if (distSq < 2500.0f) _lodTier[i] = 0;
            else if (distSq < 15000.0f) _lodTier[i] = 1;
            else _lodTier[i] = 2;
        }
    }

    // --- Phase 5 Kernel ---
    private void StepRenderPacketAssembly(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            byte lod = _lodTier[i];
            if (lod == 255)
            {
                _renderSortKey[i] = -1.0f;
            }
            else
            {
                float depth = _px[i] * 0.01f + _pz[i] * 0.99f;
                _renderSortKey[i] = depth + lod * 1000.0f + _animBone0[i] * 0.1f + _health[i] * 0.001f;
            }
        }
    }

    public void RunSerial()
    {
        StepSpatialHash(0, EntityCount);
        StepAnimationSkinning(0, AiCount);
        StepAudioPropagation(0, AudioSourceCount);
        StepAiPerception(0, AiCount);
        StepPhysicsForces(0, EntityCount);
        StepCombatDamage(0, AiCount);
        StepLocomotionSteering(0, EntityCount);
        StepHealthAndStatus(0, EntityCount);
        StepTransformIntegration(0, EntityCount);
        StepBossDirector(0, BossCount);
        StepFrustumCullingLod(0, EntityCount);
        StepRenderPacketAssembly(0, EntityCount);
    }

    public void RunParallelFor()
    {
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        // Phase 0
        RunChunkedParallel(EntityCount, 1024, opts, StepSpatialHash);
        RunChunkedParallel(AiCount, 512, opts, StepAnimationSkinning);
        StepAudioPropagation(0, AudioSourceCount);

        // Phase 1
        RunChunkedParallel(AiCount, 512, opts, StepAiPerception);
        RunChunkedParallel(EntityCount, 1024, opts, StepPhysicsForces);

        // Phase 2
        RunChunkedParallel(AiCount, 512, opts, StepCombatDamage);
        RunChunkedParallel(EntityCount, 1024, opts, StepLocomotionSteering);

        // Phase 3
        RunChunkedParallel(EntityCount, 1024, opts, StepHealthAndStatus);
        RunChunkedParallel(EntityCount, 1024, opts, StepTransformIntegration);

        // Phase 4
        StepBossDirector(0, BossCount);
        RunChunkedParallel(EntityCount, 1024, opts, StepFrustumCullingLod);

        // Phase 5
        RunChunkedParallel(EntityCount, 1024, opts, StepRenderPacketAssembly);
    }

    private static void RunChunkedParallel(int count, int chunk, ParallelOptions opts, Action<int, int> body)
    {
        int blocks = (count + chunk - 1) / chunk;
        Parallel.For(0, blocks, opts, b =>
        {
            int s = b * chunk;
            int len = Math.Min(chunk, count - s);
            body(s, len);
        });
    }

    private sealed class DelegateCohort(int id, Func<int> countProvider, Action<int, int> kernel) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => countProvider();
        protected override void ExecuteBatchLogic(int start, int length) => kernel(start, length);
    }
}
