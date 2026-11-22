using System.Numerics;
using System.Runtime.CompilerServices;
using Legio.Core;
using Legio.Core.Executio;

namespace Legio.Benchmark.Simulations;

/// <summary>
/// Multi-Agent Zero-Allocation A* Pathfinding Benchmark on a 64x64 Obstacle Maze (2,048 agents/frame).
/// Exercises:
/// - Extreme heavy-tailed per-item work variance (short 15-node patrols vs 1,500+ node cross-maze detours: ~100x skew!)
/// - Pre-allocated lock-free 64-slot scratchpad pool (0 B GC allocation for Serial, Parallel.For, and Legio)
/// - 2-Phase AI DAG: Phase 0 (A* Search) -> Phase 1 (Path Smoothing &amp; Steering Output).
/// This is the classic RTS/RPG killer workload for static work partitioning and a prime showcase for Optio stealing.
/// </summary>
public sealed class AStarNavMeshCrowdSimulation
{
    private const int ResPathOut  = 0;
    private const int ResSteerOut = 1;

    private const int GridSize = 64;
    private const int NodeCount = GridSize * GridSize; // 4096 tiles
    private const int MaxScratchSlots = 64;

    public readonly int AgentCount;

    private readonly byte[] _blocked; // 1 = wall, 0 = walkable
    private readonly int[] _startNode;
    private readonly int[] _goalNode;

    // Outputs
    private readonly int[] _nextWaypoint;
    private readonly int[] _pathLength;
    private readonly float[] _steerX;
    private readonly float[] _steerY;

    // Lock-free 64-slot scratchpad pool (bit i == 1 means slot i is FREE)
    private readonly AStarScratch[] _scratchPool;
    private long _freeScratchMask;

    private readonly PriorPilus _pathCohort;
    private readonly PriorPilus _steerCohort;

    public AStarNavMeshCrowdSimulation(int agentCount = 2_048)
    {
        AgentCount = agentCount;
        _blocked = new byte[NodeCount];
        _startNode = new int[agentCount];
        _goalNode = new int[agentCount];
        _nextWaypoint = new int[agentCount];
        _pathLength = new int[agentCount];
        _steerX = new float[agentCount];
        _steerY = new float[agentCount];

        BuildMazeWithChokepoints();

        var rng = new Random(9001);
        for (int i = 0; i < agentCount; i++)
        {
            int sx, sy, gx, gy;

            // Banded heavy-tailed queries: 12% of agents in two bands perform full cross-maze labyrinth searches!
            bool isLongCrossMaze = (i >= agentCount / 6 && i < (agentCount * 5) / 24)
                                || (i >= (agentCount * 7) / 10 && i < (agentCount * 19) / 25);

            if (isLongCrossMaze)
            {
                sx = rng.Next(1, 10);
                sy = rng.Next(1, 10);
                gx = rng.Next(GridSize - 11, GridSize - 1);
                gy = rng.Next(GridSize - 11, GridSize - 1);
            }
            else if ((i & 3) == 0)
            {
                // Medium tactical path (14..26 tiles)
                sx = rng.Next(2, GridSize - 2);
                sy = rng.Next(2, GridSize - 2);
                gx = Math.Clamp(sx + rng.Next(-22, 23), 1, GridSize - 2);
                gy = Math.Clamp(sy + rng.Next(-22, 23), 1, GridSize - 2);
            }
            else
            {
                // Short local patrol (3..8 tiles)
                sx = rng.Next(2, GridSize - 2);
                sy = rng.Next(2, GridSize - 2);
                gx = Math.Clamp(sx + rng.Next(-7, 8), 1, GridSize - 2);
                gy = Math.Clamp(sy + rng.Next(-7, 8), 1, GridSize - 2);
            }

            _startNode[i] = FindNearestWalkable(sx, sy);
            _goalNode[i] = FindNearestWalkable(gx, gy);
        }

        _scratchPool = new AStarScratch[MaxScratchSlots];
        for (int s = 0; s < MaxScratchSlots; s++)
            _scratchPool[s] = new AStarScratch(NodeCount);

        _freeScratchMask = -1L; // all 64 bits set to 1 (available)

        _pathCohort = new DelegateCohort(0, () => AgentCount, StepPathfinding);
        _steerCohort = new DelegateCohort(1, () => AgentCount, StepSteeringOutput);
    }

    private void BuildMazeWithChokepoints()
    {
        // Deterministic multi-wall obstacle course with alternating gaps forcing real A* detours
        for (int wall = 1; wall <= 5; wall++)
        {
            int x = wall * 10;
            int gapY = (wall % 2 == 1) ? 54 : 8;
            for (int y = 2; y < GridSize - 2; y++)
            {
                if (Math.Abs(y - gapY) > 3)
                    _blocked[y * GridSize + x] = 1;
            }
        }

        // Additional pillars creating local rooms
        for (int y = 16; y < 48; y += 16)
        {
            for (int x = 4; x < GridSize - 4; x++)
            {
                if ((x % 10) > 2 && (x % 10) < 8)
                    _blocked[y * GridSize + x] = 1;
            }
        }
    }

    private int FindNearestWalkable(int x, int y)
    {
        int idx = y * GridSize + x;
        if (_blocked[idx] == 0) return idx;
        for (int r = 1; r < 8; r++)
        {
            int nx = Math.Clamp(x + r, 1, GridSize - 2);
            if (_blocked[y * GridSize + nx] == 0) return y * GridSize + nx;
            int ny = Math.Clamp(y + r, 1, GridSize - 2);
            if (_blocked[ny * GridSize + x] == 0) return ny * GridSize + x;
        }
        return GridSize + 1;
    }

    public void RegisterAll(Legatus legatus)
    {
        var c0 = new Cohors(0, _pathCohort);
        c0.WriteMask.Tollere(ResPathOut);

        var c1 = new Cohors(1, _steerCohort);
        c1.ReadMask.Tollere(ResPathOut);
        c1.WriteMask.Tollere(ResSteerOut);

        legatus.RegisterCohors(c0);
        legatus.RegisterCohors(c1);
        legatus.ConstructConsilium();
    }

    private int AcquireScratch()
    {
        while (true)
        {
            long current = Volatile.Read(ref _freeScratchMask);
            if (current != 0)
            {
                int slot = BitOperations.TrailingZeroCount((ulong)current);
                long next = current & ~(1L << slot);
                if (Interlocked.CompareExchange(ref _freeScratchMask, next, current) == current)
                    return slot;
            }
            else
            {
                Thread.SpinWait(16);
            }
        }
    }

    private void ReleaseScratch(int slot)
    {
        long bit = 1L << slot;
        while (true)
        {
            long current = Volatile.Read(ref _freeScratchMask);
            if (Interlocked.CompareExchange(ref _freeScratchMask, current | bit, current) == current)
                return;
        }
    }

    private void StepPathfinding(int start, int length)
    {
        int slot = AcquireScratch();
        try
        {
            AStarScratch sc = _scratchPool[slot];
            int end = start + length;
            for (int i = start; i < end; i++)
            {
                RunAStar(sc, i, _startNode[i], _goalNode[i]);
            }
        }
        finally
        {
            ReleaseScratch(slot);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void RunAStar(AStarScratch sc, int agentIdx, int startNode, int goalNode)
    {
        if (startNode == goalNode)
        {
            _nextWaypoint[agentIdx] = goalNode;
            _pathLength[agentIdx] = 0;
            return;
        }

        int stamp = sc.NextStamp();
        int[] visited = sc.VisitedStamp;
        int[] closed = sc.ClosedStamp;
        int[] gScore = sc.GScore;
        int[] parent = sc.Parent;
        byte[] blocked = _blocked;

        int gx = goalNode & (GridSize - 1);
        int gy = goalNode >> 6;

        visited[startNode] = stamp;
        gScore[startNode] = 0;
        parent[startNode] = startNode;

        sc.HeapSize = 0;
        int h0 = (Math.Abs((startNode & (GridSize - 1)) - gx) + Math.Abs((startNode >> 6) - gy)) * 10;
        sc.Push(startNode, h0);

        int expansions = 0;
        int bestNode = startNode;
        int bestH = h0;

        while (sc.HeapSize > 0 && expansions < 2400)
        {
            int cur = sc.Pop();
            if (closed[cur] == stamp)
                continue;

            closed[cur] = stamp;
            expansions++;

            if (cur == goalNode)
            {
                bestNode = goalNode;
                break;
            }

            int cx = cur & (GridSize - 1);
            int cy = cur >> 6;
            int curG = gScore[cur];

            // 4-connected grid neighbors (unrolled for zero allocation & speed)
            int nextG = curG + 10;

            if (cx + 1 < GridSize)
                RelaxNeighbor(sc, cur + 1, cur, nextG, gx, gy, stamp, blocked, visited, closed, gScore, parent, ref bestNode, ref bestH);
            if (cx - 1 >= 0)
                RelaxNeighbor(sc, cur - 1, cur, nextG, gx, gy, stamp, blocked, visited, closed, gScore, parent, ref bestNode, ref bestH);
            if (cy + 1 < GridSize)
                RelaxNeighbor(sc, cur + GridSize, cur, nextG, gx, gy, stamp, blocked, visited, closed, gScore, parent, ref bestNode, ref bestH);
            if (cy - 1 >= 0)
                RelaxNeighbor(sc, cur - GridSize, cur, nextG, gx, gy, stamp, blocked, visited, closed, gScore, parent, ref bestNode, ref bestH);
        }

        // Backtrack to find immediate next waypoint + path length
        int steps = 0;
        int node = bestNode;
        while (node != startNode && steps < 256)
        {
            int p = parent[node];
            if (p == startNode)
                break;
            node = p;
            steps++;
        }

        _nextWaypoint[agentIdx] = node;
        _pathLength[agentIdx] = steps + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RelaxNeighbor(
        AStarScratch sc,
        int nb,
        int cur,
        int nextG,
        int gx,
        int gy,
        int stamp,
        byte[] blocked,
        int[] visited,
        int[] closed,
        int[] gScore,
        int[] parent,
        ref int bestNode,
        ref int bestH)
    {
        if (blocked[nb] != 0 || closed[nb] == stamp)
            return;

        if (visited[nb] != stamp || nextG < gScore[nb])
        {
            visited[nb] = stamp;
            gScore[nb] = nextG;
            parent[nb] = cur;

            int h = (Math.Abs((nb & (GridSize - 1)) - gx) + Math.Abs((nb >> 6) - gy)) * 10;
            if (h < bestH)
            {
                bestH = h;
                bestNode = nb;
            }

            sc.Push(nb, nextG + h);
        }
    }

    private void StepSteeringOutput(int start, int length)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            int s = _startNode[i];
            int w = _nextWaypoint[i];
            float dx = (w & (GridSize - 1)) - (s & (GridSize - 1));
            float dy = (w >> 6) - (s >> 6);
            float inv = 1.0f / MathF.Sqrt(dx * dx + dy * dy + 1e-5f);
            float urgency = 1.0f + MathF.Min(1.0f, _pathLength[i] * 0.02f);
            _steerX[i] = dx * inv * urgency;
            _steerY[i] = dy * inv * urgency;
        }
    }

    public void RunSerial()
    {
        StepPathfinding(0, AgentCount);
        StepSteeringOutput(0, AgentCount);
    }

    public void RunParallelFor()
    {
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        const int pathChunk = 64;
        int pathBlocks = (AgentCount + pathChunk - 1) / pathChunk;
        Parallel.For(0, pathBlocks, opts, b =>
        {
            int s = b * pathChunk;
            StepPathfinding(s, Math.Min(pathChunk, AgentCount - s));
        });

        const int steerChunk = 256;
        int steerBlocks = (AgentCount + steerChunk - 1) / steerChunk;
        Parallel.For(0, steerBlocks, opts, b =>
        {
            int s = b * steerChunk;
            StepSteeringOutput(s, Math.Min(steerChunk, AgentCount - s));
        });
    }

    private sealed class AStarScratch
    {
        public readonly int[] VisitedStamp;
        public readonly int[] ClosedStamp;
        public readonly int[] GScore;
        public readonly int[] Parent;
        private readonly int[] _heapNode;
        private readonly int[] _heapF;
        public int HeapSize;
        private int _stamp;

        public AStarScratch(int nodeCount)
        {
            VisitedStamp = new int[nodeCount];
            ClosedStamp = new int[nodeCount];
            GScore = new int[nodeCount];
            Parent = new int[nodeCount];
            _heapNode = new int[nodeCount * 2];
            _heapF = new int[nodeCount * 2];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int NextStamp()
        {
            _stamp++;
            if (_stamp == int.MaxValue)
            {
                Array.Clear(VisitedStamp);
                Array.Clear(ClosedStamp);
                _stamp = 1;
            }
            return _stamp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(int node, int f)
        {
            int idx = HeapSize;
            if (idx >= _heapNode.Length)
                return;

            HeapSize = idx + 1;
            while (idx > 0)
            {
                int parent = (idx - 1) >> 1;
                if (_heapF[parent] <= f)
                    break;
                _heapNode[idx] = _heapNode[parent];
                _heapF[idx] = _heapF[parent];
                idx = parent;
            }

            _heapNode[idx] = node;
            _heapF[idx] = f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Pop()
        {
            int top = _heapNode[0];
            int lastIdx = --HeapSize;
            if (lastIdx > 0)
            {
                int node = _heapNode[lastIdx];
                int f = _heapF[lastIdx];
                int idx = 0;

                while (true)
                {
                    int left = (idx << 1) + 1;
                    if (left >= lastIdx) break;
                    int right = left + 1;
                    int smallest = (right < lastIdx && _heapF[right] < _heapF[left]) ? right : left;
                    if (f <= _heapF[smallest]) break;

                    _heapNode[idx] = _heapNode[smallest];
                    _heapF[idx] = _heapF[smallest];
                    idx = smallest;
                }

                _heapNode[idx] = node;
                _heapF[idx] = f;
            }

            return top;
        }
    }

    private sealed class DelegateCohort(int id, Func<int> countProvider, Action<int, int> kernel) : PriorPilus(id)
    {
        protected override int GetTotalItemCount() => countProvider();
        protected override void ExecuteBatchLogic(int start, int length) => kernel(start, length);
    }
}
