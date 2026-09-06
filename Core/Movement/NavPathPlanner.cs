using System.Numerics;

namespace AuraCanAI.Dalamud.Core.Movement;

/// <summary>轴对齐障碍矩形(世界平面坐标;供寻路用的独立模型,不依赖游戏配置类型,便于单测)。</summary>
public readonly record struct NavObstacle(float MinX, float MinZ, float MaxX, float MaxZ)
{
	public float CenterX => (MinX + MaxX) * 0.5f;
	public float CenterZ => (MinZ + MaxZ) * 0.5f;
}

/// <summary>
/// 2D 栅格 A* 寻路 + 路径拉直(纯逻辑,无 Dalamud 依赖,可单测)。
/// 适用场景:房区室内,障碍为手画轴对齐矩形(家具),区域几十米内。
/// 流程:
///   1) 障碍按通行余量(pad)膨胀 → 栅格标记阻塞格;
///   2) 8 邻 A*(禁止斜穿墙角)从起点搜到终点;
///   3) 回溯得格路径 → 世界坐标;
///   4) LOS 拉直:贪心保留"不可直达"的拐点,输出少量直线段。
/// 输出的折线路径由上层"前瞻点跟随"消费(朝路径上前方一点走,转弯自然成弧),本类不涉及移动。
///
/// 边界处理(实机教训,勿删):
/// - 起点格强制可达(玩家真实站立位置必然是可达格);若起点在真实家具矩形内(坐姿起身/矩形画大了),
///   自动"挤出"——从起点向最近自由格打通一条 1 格宽通道(上限 8 格);
/// - 终点被膨胀盖住但不在真实家具内(贴家具站/坐)→ 终点格与其邻居解除阻塞,让 A* 能贴边到达;
/// - 终点在真实家具内 → 外推到最近自由格作为实际落点(上限 6 格),世界路径以该格收尾(不加回原终点,
///   避免末段直线穿家具);上层(坐/走近)用自身距离判定收尾;
/// - 障碍全堵死/终点无法外推 → 返回 null(调用方决定:直线试走一次或放弃)。
/// </summary>
public static class NavPathPlanner
{
	/// <summary>栅格分辨率(米/格)</summary>
	public const float Cell = 0.2f;

	/// <summary>障碍膨胀余量(米):角色半径 + 通过余量。与旧 FindBlocking margin 一致;
	/// ⚠️ 勿调大——桌子旁的座位空间常只有 0.4m,Pad 过大会把真实的起/终点站格包进阻塞区导致无解。</summary>
	public const float Pad = 0.4f;

	/// <summary>栅格单边最大格数(防御:正常房间远小于此;超限裁剪到只含起终点的窗口)</summary>
	private const int MaxCellsPerSide = 600;

	/// <summary>终点在真实家具内时的外推搜索半径(格)</summary>
	private const int GoalExtrudeMaxCells = 6;

	/// <summary>起点在真实家具内时的挤出通道长度上限(格)</summary>
	private const int StartExtrudeMaxCells = 8;

	/// <summary>两个世界坐标是否互相直达(线段不穿透任何膨胀障碍)。</summary>
	public static bool LineOfSight(in Vector3 a, in Vector3 b, IReadOnlyList<NavObstacle> obstacles)
	{
		foreach (var o in obstacles)
		{
			if (SegmentHitsExpandedRect(a.X, a.Z, b.X, b.Z, o.MinX, o.MinZ, o.MaxX, o.MaxZ, Pad)) return false;
		}
		return true;
	}

	/// <summary>寻路主入口:返回世界坐标路径点列(含起点与终点落点;已拉直;len≥2);无解返回 null。</summary>
	public static List<Vector3>? FindPath(Vector3 start, Vector3 goal, IReadOnlyList<NavObstacle> obstacles)
	{
		// 快速路径:没有障碍或起终点间直线不穿任何膨胀矩形 → 直接两点
		if (obstacles.Count == 0 || LineOfSight(start, goal, obstacles))
			return new List<Vector3> { start, goal };

		// 1) 栅格窗口:包含所有膨胀障碍 + 起终点,外扩缓冲
		float minX = MathF.Min(start.X, goal.X), maxX = MathF.Max(start.X, goal.X);
		float minZ = MathF.Min(start.Z, goal.Z), maxZ = MathF.Max(start.Z, goal.Z);
		foreach (var o in obstacles)
		{
			minX = MathF.Min(minX, o.MinX - Pad); maxX = MathF.Max(maxX, o.MaxX + Pad);
			minZ = MathF.Min(minZ, o.MinZ - Pad); maxZ = MathF.Max(maxZ, o.MaxZ + Pad);
		}
		const float buffer = Cell * 3f;
		minX -= buffer; maxX += buffer; minZ -= buffer; maxZ += buffer;

		var w = Math.Max((int)MathF.Ceiling((maxX - minX) / Cell), 4);
		var h = Math.Max((int)MathF.Ceiling((maxZ - minZ) / Cell), 4);
		if (w > MaxCellsPerSide || h > MaxCellsPerSide)
		{
			// 超限(罕见:障碍散布极广):裁剪窗口只覆盖起终点走廊,防爆内存
			var c = MathF.Max(4f, Pad);
			minX = MathF.Min(start.X, goal.X) - c; maxX = MathF.Max(start.X, goal.X) + c;
			minZ = MathF.Min(start.Z, goal.Z) - c; maxZ = MathF.Max(start.Z, goal.Z) + c;
			w = Math.Max((int)MathF.Ceiling((maxX - minX) / Cell), 4);
			h = Math.Max((int)MathF.Ceiling((maxZ - minZ) / Cell), 4);
		}

		int ToIndex(int gx, int gz) => gz * w + gx;
		bool InBounds(int gx, int gz) => gx >= 0 && gx < w && gz >= 0 && gz < h;

		var startGx = Math.Clamp((int)MathF.Floor((start.X - minX) / Cell), 0, w - 1);
		var startGz = Math.Clamp((int)MathF.Floor((start.Z - minZ) / Cell), 0, h - 1);
		var goalGx = Math.Clamp((int)MathF.Floor((goal.X - minX) / Cell), 0, w - 1);
		var goalGz = Math.Clamp((int)MathF.Floor((goal.Z - minZ) / Cell), 0, h - 1);

		// 2) 标记阻塞:膨胀矩形硬阻塞(blocked);真实矩形另存(real)供"挤出/外推"判断
		var blocked = new bool[w * h];
		var real = new bool[w * h];
		foreach (var o in obstacles)
		{
			MarkRange(blocked, w, h, o.MinX - Pad, o.MinZ - Pad, o.MaxX + Pad, o.MaxZ + Pad, minX, minZ);
			MarkRange(real, w, h, o.MinX, o.MinZ, o.MaxX, o.MaxZ, minX, minZ);
		}

		// 3) 起点/终点格处理
		var startIdx = ToIndex(startGx, startGz);
		var goalIdx = ToIndex(goalGx, goalGz);

		// 起点在真实家具内(坐姿起身/矩形画大):向最近自由格打通 1 格宽通道(可穿阻塞 BFS,上限 8 格)
		if (real[startIdx])
		{
			var esc = BfsToFree(startIdx, w, h, ToIndex, InBounds, blocked);
			if (esc == null) return null; // 四周全堵,真无路
			foreach (var idx in esc) blocked[idx] = false;
		}

		// 终点在真实家具内:外推到最近自由格作为落点;仅被膨胀盖住:解除终点格+邻居阻塞(贴边可达)
		var goalReal = real[goalIdx];
		if (goalReal)
		{
			var free = FindFreeNear(goalIdx, w, h, ToIndex, InBounds, blocked);
			if (free == null) return null; // 被家具围死
			goalGx = free.Value % w; goalGz = free.Value / w;
		}
		else if (blocked[goalIdx])
		{
			// 贴家具站/坐:膨胀盖住终点格 → 放行终点格及其 4 邻(仍在真实家具外的部分)
			blocked[goalIdx] = false;
			foreach (var (dx, dz) in Dir4)
			{
				var nx = goalGx + dx; var nz = goalGz + dz;
				if (InBounds(nx, nz) && !real[ToIndex(nx, nz)]) blocked[ToIndex(nx, nz)] = false;
			}
		}
		goalIdx = ToIndex(goalGx, goalGz);
		blocked[startIdx] = false;

		// 4) A* 搜索
		var cellPath = AStar(startIdx, goalIdx, w, h, ToIndex, InBounds, blocked);
		if (cellPath == null) return null;

		// 5) 格 → 世界坐标(起点用真实坐标;终点用落点格中心——若落点即原终点格,直接加 goal 精确值)
		var world = new List<Vector3> { start };
		var goalCellIsOriginal = goalGx == (int)MathF.Floor((goal.X - minX) / Cell) && goalGz == (int)MathF.Floor((goal.Z - minZ) / Cell);
		for (var i = 1; i < cellPath.Count - 1; i++)
		{
			var idx = cellPath[i];
			var gx = idx % w; var gz = idx / w;
			world.Add(new Vector3(minX + (gx + 0.5f) * Cell, start.Y, minZ + (gz + 0.5f) * Cell));
		}
		world.Add(goalCellIsOriginal ? goal
			: new Vector3(minX + (goalGx + 0.5f) * Cell, start.Y, minZ + (goalGz + 0.5f) * Cell));
		if (world.Count == 1) world.Add(goal);

		// 6) LOS 拉直
		return Straighten(world, obstacles);
	}

	// 4 邻方向(用于挤出/外推)
	private static readonly (int dx, int dz)[] Dir4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };
	// 8 邻方向
	private static readonly (int dx, int dz)[] Dir8 =
	{
		(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)
	};

	private static void MarkRange(bool[] grid, int w, int h, float x0, float z0, float x1, float z1, float minX, float minZ)
	{
		var gx0 = Math.Clamp((int)MathF.Floor((x0 - minX) / Cell), 0, w - 1);
		var gz0 = Math.Clamp((int)MathF.Floor((z0 - minZ) / Cell), 0, h - 1);
		var gx1 = Math.Clamp((int)MathF.Floor((x1 - minX) / Cell), 0, w - 1);
		var gz1 = Math.Clamp((int)MathF.Floor((z1 - minZ) / Cell), 0, h - 1);
		for (var gz = gz0; gz <= gz1; gz++)
			for (var gx = gx0; gx <= gx1; gx++)
				grid[gz * w + gx] = true;
	}

	/// <summary>从 start(在阻塞内)4 邻 BFS(可穿阻塞)找最近自由格,返回 start→自由格 的格链(含两端);
	/// 深度上限 StartExtrudeMaxCells 层;全堵返回 null。</summary>
	private static List<int>? BfsToFree(int start, int w, int h,
		Func<int, int, int> idx, Func<int, int, bool> inBounds, bool[] blocked)
	{
		var parent = new Dictionary<int, int>();
		var q = new Queue<int>();
		q.Enqueue(start);
		var visited = new HashSet<int> { start };
		for (var depth = 0; depth <= StartExtrudeMaxCells; depth++)
		{
			var layer = q.Count;
			if (layer == 0) break;
			for (var l = 0; l < layer; l++)
			{
				var cur = q.Dequeue();
				var cx = cur % w; var cz = cur / w;
				foreach (var (dx, dz) in Dir4)
				{
					var nx = cx + dx; var nz = cz + dz;
					if (!inBounds(nx, nz)) continue;
					var ni = idx(nx, nz);
					if (!visited.Add(ni)) continue;
					parent[ni] = cur;
					if (!blocked[ni]) // 找到自由格 → 回溯链
					{
						var chain = new List<int> { ni };
						var node = cur;
						while (node != start) { chain.Add(node); node = parent[node]; }
						chain.Add(start);
						chain.Reverse();
						return chain;
					}
					q.Enqueue(ni);
				}
			}
		}
		return null;
	}

	/// <summary>终点在真实家具内:向外找最近自由格(格距离上限 GoalExtrudeMaxCells)。</summary>
	private static int? FindFreeNear(int start, int w, int h,
		Func<int, int, int> idx, Func<int, int, bool> inBounds, bool[] blocked)
	{
		var sx = start % w; var sz = start / w;
		for (var radius = 0; radius <= GoalExtrudeMaxCells; radius++)
		{
			for (var dz = -radius; dz <= radius; dz++)
			{
				for (var dx = -radius; dx <= radius; dx++)
				{
					if (MathF.Abs(dx) != radius && MathF.Abs(dz) != radius) continue; // 只扫环
					var nx = sx + dx; var nz = sz + dz;
					if (!inBounds(nx, nz)) continue;
					var ni = idx(nx, nz);
					if (!blocked[ni]) return ni;
				}
			}
		}
		return null;
	}

	/// <summary>A*:返回格索引路径(含起点与终点);无解返回 null。8 邻,禁止斜穿墙角。</summary>
	private static List<int>? AStar(int start, int goal, int w, int h,
		Func<int, int, int> idx, Func<int, int, bool> inBounds, bool[] blocked)
	{
		var gScore = new Dictionary<int, float> { [start] = 0f };
		var cameFrom = new Dictionary<int, int>();
		var open = new PriorityQueue<int, float>();
		var closed = new HashSet<int>();
		open.Enqueue(start, 0f);

		int? found = null;
		while (open.Count > 0)
		{
			var cur = open.Dequeue();
			if (closed.Contains(cur)) continue; // 堆中旧副本
			if (cur == goal) { found = cur; break; }
			closed.Add(cur);
			var cx = cur % w; var cz = cur / w;
			foreach (var (dx, dz) in Dir8)
			{
				var nx = cx + dx; var nz = cz + dz;
				if (!inBounds(nx, nz)) continue;
				var ni = idx(nx, nz);
				if (closed.Contains(ni) || blocked[ni]) continue;
				if (dx != 0 && dz != 0) // 斜穿墙角检查
				{
					if (blocked[idx(cx + dx, cz)] || blocked[idx(cx, cz + dz)]) continue;
				}
				var step = (dx != 0 && dz != 0) ? 1.4142f : 1f;
				var tentative = gScore[cur] + step;
				if (!gScore.TryGetValue(ni, out var g) || tentative < g)
				{
					gScore[ni] = tentative;
					cameFrom[ni] = cur;
					var hx = nx - (goal % w); var hz = nz - (goal / w);
					var heuristic = MathF.Sqrt(hx * hx + hz * hz);
					open.Enqueue(ni, tentative + heuristic);
				}
			}
		}
		if (found == null) return null;

		var path = new List<int>();
		var node = found.Value;
		while (node != start) { path.Add(node); node = cameFrom[node]; }
		path.Add(start);
		path.Reverse();
		return path;
	}

	/// <summary>贪心拉直:尽可能直连(不穿膨胀障碍)去掉中间格点,输出直线段折线。</summary>
	private static List<Vector3> Straighten(List<Vector3> path, IReadOnlyList<NavObstacle> obstacles)
	{
		var result = new List<Vector3> { path[0] };
		var i = 0;
		while (i < path.Count - 1)
		{
			var j = path.Count - 1;
			while (j > i + 1 && !LineOfSight(path[i], path[j], obstacles)) j--;
			result.Add(path[j]);
			i = j;
		}
		return result;
	}

	/// <summary>线段 a→b 是否穿过膨胀后的矩形 (minX,minZ)-(maxX,maxZ)。Slab 法。</summary>
	private static bool SegmentHitsExpandedRect(float ax, float az, float bx, float bz,
		float minX, float minZ, float maxX, float maxZ, float pad)
	{
		minX -= pad; maxX += pad; minZ -= pad; maxZ += pad;
		float tmin = 0f, tmax = 1f;
		var dx = bx - ax; var dz = bz - az;
		if (!Slab(ax, dx, minX, maxX, ref tmin, ref tmax)) return false;
		if (!Slab(az, dz, minZ, maxZ, ref tmin, ref tmax)) return false;
		return true;
	}

	private static bool Slab(float origin, float dir, float lo, float hi, ref float tmin, ref float tmax)
	{
		if (MathF.Abs(dir) < 1e-6f) return origin >= lo && origin <= hi; // 平行且不在带内 = 不交
		var t1 = (lo - origin) / dir; var t2 = (hi - origin) / dir;
		if (t1 > t2) (t1, t2) = (t2, t1);
		tmin = MathF.Max(tmin, t1);
		tmax = MathF.Min(tmax, t2);
		return tmin <= tmax;
	}
}
