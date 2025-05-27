using Nebukam.Common;
#if UNITY_EDITOR
using Nebukam.Common.Editor;
#endif
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;
using Random = UnityEngine.Random;

namespace Nebukam.ORCA
{
    /// <summary>
    /// ORCA模拟场景设置脚本
    /// </summary>
    public class ORCASetup : MonoBehaviour
    {
        // 代理组
        private AgentGroup<Agent> agents;

        // 静态障碍物组
        private ObstacleGroup obstacles;

        // 动态障碍物组
        private ObstacleGroup dynObstacles;

        // 射线检测组
        private RaycastGroup raycasts;

        // ORCA模拟器
        private ORCA simulation;

        [Header("模拟设置")] public int seed = 12345;
        public Transform target;
        public AxisPair axis = AxisPair.XZ;

        [Header("代理设置")] public int agentCount;
        public float maxAgentRadius = 2f;
        public float maxSpeed = 1f;
        public float minSpeed = 1f;

        [Header("障碍物设置")] public int obstacleCount = 100;
        public int dynObstacleCount;
        public float maxObstacleRadius = 2f;
        public int minObstacleEdgeCount = 2;
        public int maxObstacleEdgeCount = 2;
        public float2 min, max; // 障碍物生成边界

        [Header("调试设置")] Color staticObstacleColor = Color.red;
        Color dynObstacleColor = Color.yellow;

        [Header("射线检测设置")] public int raycastCount = 50;
        public float raycastDistance = 10f;

        /// <summary>
        /// 当脚本实例被加载时调用此方法
        /// 初始化模拟组件
        /// </summary>
        private void Awake()
        {
            // 初始化代理组
            agents = new AgentGroup<Agent>();
            // 初始化静态障碍物组
            obstacles = new ObstacleGroup();
            // 初始化动态障碍物组
            dynObstacles = new ObstacleGroup();
            // 初始化射线检测组
            raycasts = new RaycastGroup();
            // 创建ORCA模拟器实例
            simulation = new ORCA();
            simulation.plane = axis; // 设置平面轴对
            simulation.agents = agents; // 设置代理组
            simulation.staticObstacles = obstacles; // 设置静态障碍物组
            simulation.dynamicObstacles = dynObstacles; // 设置动态障碍物组
            simulation.raycasts = raycasts; // 设置射线组
        }

        /// <summary>
        /// 初始化游戏对象
        /// 包括创建静态和动态障碍物、边界框、代理和射线检测
        /// </summary>
        private void Start()
        {
            // 初始化随机数生成器
            Random.InitState(seed);

            #region 创建静态障碍物

            float dirRange = 2f; // 障碍物方向范围
            List<float3> vList = new List<float3>();
            Obstacle o;

            for (int i = 0; i < obstacleCount; i++)
            {
                int vCount = Random.Range(minObstacleEdgeCount, maxObstacleEdgeCount);
                vList.Clear();
                // 随机生成障碍物顶点
                vList.Capacity = vCount; //容量 预分配空间

                // 构建类似树枝形状的障碍物

                float3 start = float3(Random.Range(min.x, max.x), Random.Range(min.y, max.y), 0f),
                    pt = start,
                    dir = float3(Random.Range(-dirRange, dirRange), Random.Range(-dirRange, dirRange), 0f);

                if (axis == AxisPair.XZ)
                {
                    pt = start = float3(start.x, 0f, start.y);
                    dir = float3(dir.x, 0f, dir.y);
                }

                vList.Add(start);
                vCount--;

                for (int j = 0; j < vCount; j++)
                {
                    dir = normalize(Maths.RotateAroundPivot(dir, float3(0f),
                        axis == AxisPair.XY ? float3(0f, 0f, (math.PI) / vCount) : float3(0f, (math.PI) / vCount, 0f)));

                    pt = pt + dir * Random.Range(1f, maxObstacleRadius);
                    vList.Add(pt);
                }

                // 如果顶点数不等于2，则添加起点形成闭合形状
                //if (vCount != 2) { vList.Add(start); }

                o = obstacles.Add(vList, axis == AxisPair.XZ);
            }

            #endregion

            #region 创建包围方形边界

            float3[] squarePoints = new float3[]
            {
                float3(min.x, min.y, 0f) * 1.2f,
                float3(min.x, max.y, 0f) * 1.2f,
                float3(max.x, max.y, 0f) * 1.2f,
                float3(max.x, min.y, 0f) * 1.2f,
            };

            if (axis == AxisPair.XZ)
            {
                for (int i = 0; i < squarePoints.Length; i++)
                    squarePoints[i] = float3(squarePoints[i].x, 0f, squarePoints[i].y);
            }

            // 添加边界障碍物，设置不闭合边缘，厚度为10.0
            obstacles.Add(squarePoints, false, 10.0f);

            #endregion

            // 重置随机种子用于动态障碍物
            Random.InitState(seed + 10);

            #region 创建动态障碍物

            for (int i = 0; i < dynObstacleCount; i++)
            {
                int vCount = Random.Range(minObstacleEdgeCount, maxObstacleEdgeCount);
                vList.Clear();
                vList.Capacity = vCount;

                // 构建类似树枝形状的障碍物

                float3 start = float3(Random.Range(min.x, max.x), Random.Range(min.y, max.y), 0f),
                    pt = start,
                    dir = float3(Random.Range(-dirRange, dirRange), Random.Range(-dirRange, dirRange), 0f);

                if (axis == AxisPair.XZ)
                {
                    pt = start = float3(start.x, 0f, start.y);
                    dir = float3(dir.x, 0f, dir.y);
                }

                vList.Add(start);
                vCount--;

                for (int j = 0; j < vCount; j++)
                {
                    dir = normalize(Maths.RotateAroundPivot(dir, float3(0f),
                        axis == AxisPair.XY ? float3(0f, 0f, (math.PI) / vCount) : float3(0f, (math.PI) / vCount, 0f)));
                    pt = pt + dir * Random.Range(1f, maxObstacleRadius);
                    vList.Add(pt);
                }

                // 如果顶点数不等于2，则添加起点形成闭合形状
                //if (vCount != 2) { vList.Add(start); }

                dynObstacles.Add(vList, axis == AxisPair.XZ);
            }

            #endregion

            #region 创建代理

            float inc = Maths.TAU / (float)agentCount; 
            IAgent a;

            for (int i = 0; i < agentCount; i++)
            {
                if (axis == AxisPair.XY)
                {
                    a = agents.Add((float3)transform.position + float3(Random.value, Random.value, 0f)) as IAgent;
                }
                else if (axis == AxisPair.XZ)
                {
                    a = agents.Add((float3)transform.position + float3(Random.value, 0f, Random.value)) as IAgent;
                }
                else
                {
                    Debug.LogError("手动设置自定义枚举值");
                    return;
                }

                // 设置代理半径和障碍物半径
                a.radius = 0.5f + Random.value * maxAgentRadius;
                a.radiusObst = a.radius + Random.value * maxAgentRadius;
                a.prefVelocity = float3(0f);
            }

            #endregion

            #region 创建射线检测

            Raycast r;

            for (int i = 0; i < raycastCount; i++)
            {
                if (axis == AxisPair.XY)
                {
                    r = raycasts.Add(float3(Random.Range(min.x, max.x), Random.Range(min.y, max.y), 0f)) as Raycast;
                    r.dir = normalize(float3(Random.Range(-1f, 1f), Random.Range(-1f, 1f), 0f));
                }
                else
                {
                    r = raycasts.Add(float3(Random.Range(min.x, max.x), 0f, Random.Range(min.y, max.y))) as Raycast;
                    r.dir = normalize(float3(Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f)));
                }

                r.distance = raycastDistance;
            }

            #endregion
        }

        /// <summary>
        /// 每帧调用一次
        /// 用于更新模拟以及绘制调试信息
        /// </summary>
        private void Update()
        {
            // 调度模拟任务
            simulation.Schedule(Time.deltaTime);

            // 存储目标位置
            float3 tr = target.position;

            #region 更新和绘制代理

            // 绘制代理调试信息（仅在编辑器模式下）
            IAgent agent;
            float3 agentPos;
            for (int i = 0, count = agents.Count; i < count; i++)
            {
                agent = agents[i] as IAgent;
                agentPos = agent.pos;

#if UNITY_EDITOR
                // 绘制代理身体
                if (axis == AxisPair.XY)
                {
                    Draw.Circle2D(agentPos, agent.radius, Color.green, 12);
                    Draw.Circle2D(agentPos, agent.radiusObst, Color.cyan.A(0.15f), 12);
                }
                else
                {
                    Draw.Circle(agentPos, agent.radius, Color.green, 12);
                    Draw.Circle(agentPos, agent.radiusObst, Color.cyan.A(0.15f), 12);
                }

                // 绘制代理模拟速度(ORCA合规)
                Draw.Line(agentPos, agentPos + (normalize(agent.velocity) * agent.radius), Color.green);
                // 绘制代理目标向量
                Draw.Line(agentPos, agentPos + (normalize(agent.prefVelocity) * agent.radius), Color.grey);
#endif
                // 更新代理首选速度，使其始终尝试到达"target"对象
                float mspd = max(minSpeed + (i + 1) * 0.5f, maxSpeed);
                float s = min(1f, distance(agent.pos, tr) / mspd);
                float agentSpeed = mspd * s;
                agent.maxSpeed = agentSpeed * s;
                agent.prefVelocity = normalize(tr - agent.pos) * agentSpeed;
            }

            #endregion

#if UNITY_EDITOR

            #region 绘制障碍物

            // 绘制静态障碍物
            Obstacle o;
            int oCount = obstacles.Count, subCount;
            for (int i = 0; i < oCount; i++)
            {
                o = obstacles[i];
                subCount = o.Count;

                // 绘制每个线段
                for (int j = 1, count = o.Count; j < count; j++)
                {
                    Draw.Line(o[j - 1].pos, o[j].pos, staticObstacleColor);
                    Draw.Circle(o[j - 1].pos, 0.2f, Color.magenta, 6);
                }

                // 绘制闭合线段(模拟认为2+线段是闭合的)
                if (!o.edge)
                    Draw.Line(o[subCount - 1].pos, o[0].pos, staticObstacleColor);
            }

            float delta = Time.deltaTime * 50f;

            // 绘制动态障碍物
            oCount = dynObstacles.Count;
            for (int i = 0; i < oCount; i++)
            {
                o = dynObstacles[i];
                subCount = o.Count;

                // 绘制每个线段
                for (int j = 1, count = o.Count; j < count; j++)
                {
                    Draw.Line(o[j - 1].pos, o[j].pos, dynObstacleColor);
                }

                // 绘制闭合线段(模拟认为2+线段是闭合的)
                if (!o.edge)
                    Draw.Line(o[subCount - 1].pos, o[0].pos, dynObstacleColor);
            }

            #endregion

            #region 更新和绘制射线检测

            Raycast r;
            float rad = 0.2f;
            for (int i = 0, count = raycasts.Count; i < count; i++)
            {
                r = raycasts[i] as Raycast;
                Draw.Circle2D(r.pos, rad, Color.white, 3);
                if (r.anyHit)
                {
                    Draw.Line(r.pos, r.pos + r.dir * r.distance, Color.white.A(0.5f));

                    if (axis == AxisPair.XY)
                    {
                        if (r.obstacleHit != null)
                        {
                            Draw.Circle2D(r.obstacleHitLocation, rad, Color.cyan, 3);
                        }

                        if (r.agentHit != null)
                        {
                            Draw.Circle2D(r.agentHitLocation, rad, Color.magenta, 3);
                        }
                    }
                    else
                    {
                        if (r.obstacleHit != null)
                        {
                            Draw.Circle(r.obstacleHitLocation, rad, Color.cyan, 3);
                        }

                        if (r.agentHit != null)
                        {
                            Draw.Circle(r.agentHitLocation, rad, Color.magenta, 3);
                        }
                    }
                }
                else
                {
                    Draw.Line(r.pos, r.pos + r.dir * r.distance, Color.blue.A(0.5f));
                }
            }

            #endregion

#endif
        }

        /// <summary>
        /// 在Update之后调用
        /// 尝试完成并应用模拟结果，仅当作业完成时
        /// TryComplete不会强制完成作业
        /// </summary>
        private void LateUpdate()
        {
            if (simulation.TryComplete())
            {
                // 随机移动动态障碍物
                int oCount = dynObstacles.Count;
                float delta = Time.deltaTime * 50f;

                for (int i = 0; i < oCount; i++)
                    dynObstacles[i].Offset(float3(Random.Range(-delta, delta), Random.Range(-delta, delta), 0f));
            }
        }

        /// <summary>
        /// 当应用程序退出时调用
        /// 确保清理所有作业
        /// </summary>
        private void OnApplicationQuit()
        {
            simulation.DisposeAll();
        }
    }
}