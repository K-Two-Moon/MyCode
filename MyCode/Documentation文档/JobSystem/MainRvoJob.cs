using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

namespace RvoLib
{
    /// <summary>
    /// 主RVO(互惠速度障碍)计算Job
    /// 实现基于线性规划的RVO核心算法
    /// </summary>
    [BurstCompile]
    public unsafe struct MainRvoJob : IJobChunk
    {
        /// <summary>时间步长</summary>
        public float dt;
        /// <summary>RVO搜索数组头指针</summary>
        [ReadOnly,NativeDisableUnsafePtrRestriction]public CdRvo* flatSearchArrayHeadPtr; 
        /// <summary>RVO组件类型句柄</summary>
        public ComponentTypeHandle<CdRvo> HRvo;
        /// <summary>邻居缓冲区类型句柄</summary>
        public BufferTypeHandle<BENeighbor> HNeighbor;

        /// <summary>
        /// Job执行入口
        /// 处理每个chunk中的实体，计算RVO速度
        /// </summary>
        void IJobChunk.Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var rvos = chunk.GetComponentDataPtrRW(ref HRvo);
            var neighborLists = chunk.GetBufferAccessor(ref HNeighbor);
            var enumerator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (enumerator.NextEntityIndex(out int bIndex)) 
            {
                var rvoPtr = rvos + bIndex;
               
                #region [MainRvo新增]边界检查
                if (rvoPtr->bounds.isValid == 1)
                {
                    var infoPtr = &rvoPtr->info;
                    var neighborList = neighborLists[bIndex];
                    var iterValuesPtr = &rvoPtr->iterValues;
                    var myPosition = rvoPtr->info.position;
                    var myPrefVelocity = infoPtr->prefVelocity;
                    var mySumForce = iterValuesPtr->sumForce;
                    do 
                    {
                        // 【MassAndPush新增】最终移动矢量=rvo+力干扰
                        ///如果当前有力量，为了营造卡肉感，得把期望速度降低一些
                        ///这纯脆是个效果考量
                        if (lengthsq(mySumForce) > float.Epsilon) 
                        {
                            var prefVelocityDir = normalizesafe(myPrefVelocity);
                            var prefVelocityLen = length(myPrefVelocity);
                            myPrefVelocity = prefVelocityDir * prefVelocityLen * 0.1f;
                        }


                        ///周围没邻居或者无视rvo影响
                        if (infoPtr->maxNeighbors == 0 || !infoPtr->considerOthersRVO)
                        {
                            iterValuesPtr->velocity = myPrefVelocity
                                + iterValuesPtr->sumForce;//【MassAndPush新增】最终移动矢量=rvo+力干扰
                            continue;
                        }

                        var in_myVelocity = iterValuesPtr->velocity;
                        var myRadius = infoPtr->radius;
                        var myMass = infoPtr->mass; //【MassAndPush新增】
                        //按理说有多少个邻居就要有多少个RVOLine，
                        //但是有的邻居无法生成合法的Line，因此要计数
                        //这一部分虽然说是核心算法，但是难度也很大，需要建立在对RVO算法非常理解的基础上
                        //但是不理解也没有关系，是论文算法，比较稳定，我把其中的一些边界情况处理了，大家可以直接不求甚解地用
                        //我个人还是很讨厌不求甚解的，第一，新需求加不了，第二，有bug该不了；
                        //但是在我弄明白之后，我发现弄明白没鸟用，明白了你也改不了，获奖的论文算法，只有能力理解，没有能力改变
                        var myNeighborsCount = neighborList.Length;
                        var rvoLines = new NativeList<RVOLine>(myNeighborsCount, Allocator.Temp);
                        var numObstLines = 0;
                        var invTimeHorizon = 1.0f / infoPtr->timeHorizon; //我避让别人的时间窗口，
                        for (int i = 0; i < myNeighborsCount; i++) 
                        {
                            var neighorIndex = neighborList[i].flatSearchArrayIndex;
                            var neighorPtr = flatSearchArrayHeadPtr + neighorIndex;
                            var neighorPosition = neighorPtr->info.position;
                            var neighorVelocity = neighorPtr->iterValues.velocity; //这里，计算我这一帧的速度，其实用的是邻居上一帧的结果
                            var relativePos = neighorPosition - myPosition;
                            var relativeVelocity = in_myVelocity - neighorVelocity;//这里，计算我这一帧的速度，其实用的是邻居上一帧的结果
                            var distanceSq = lengthsq(relativePos);
                            var sumRadius = myRadius + neighorPtr->info.radius;
                            var sumRadiusSq = lengthsq(sumRadius);
                            var line = new RVOLine();
                            var lineValid = true;
                            float2 u = default;
                            if (distanceSq > sumRadiusSq) //一下是论文算法
                            {
                                //这个分支的意思是
                                //如果我和邻居没有发生碰撞，则应该产生“绕行行为”
                                //绕行可以理解为：以现在的速度大小继续开下去，在我规避的时间窗口内不发生碰撞
                                //此时我能改变的只有速度方向

                                // No collision.
                                float2 w = relativeVelocity - invTimeHorizon * relativePos;

                                // Vector from cutoff center to relative velocity.
                                float wLengthSq = lengthsq(w);
                                float dotProduct1 = dot(w, relativePos);

                                if (dotProduct1 < 0.0f && lengthsq(dotProduct1) > sumRadiusSq * wLengthSq)
                                {
                                    // Project on cut-off circle.
                                    float wLength = sqrt(wLengthSq);
                                    float2 unitW = w / wLength;

                                    line.dir = float2(unitW.y, -unitW.x);
                                    u = (sumRadius * invTimeHorizon - wLength) * unitW;
                                }
                                else
                                {
                                    // Project on legs.
                                    float leg = sqrt(distanceSq - sumRadiusSq);

                                    if (Det(relativePos, w) > 0.0f)
                                    {
                                        // Project on left leg.
                                        line.dir = float2(relativePos.x * leg - relativePos.y * sumRadius, relativePos.x * sumRadius + relativePos.y * leg) / distanceSq;
                                    }
                                    else
                                    {
                                        // Project on right leg.
                                        line.dir = -float2(relativePos.x * leg + relativePos.y * sumRadius, -relativePos.x * sumRadius + relativePos.y * leg) / distanceSq;
                                    }

                                    float dotProduct2 = dot(relativeVelocity, line.dir);
                                    u = dotProduct2 * line.dir - relativeVelocity;
                                }
                            }
                            else
                            {
                                ///碰撞发生了，应该尽量赶紧让开
                                // Collision. Project on cut-off circle of time timeStep.
                                float invTimeStep = 1.0f / dt;

                                // Vector from cutoff center to relative velocity.
                                var w = relativeVelocity - invTimeStep * relativePos;
                                var wLength = length(w);
                                if (wLength < float.Epsilon)
                                {
                                    lineValid = false;
                                }
                                else
                                {
                                    var unitW = w / wLength;
                                    line.dir = float2(unitW.y, -unitW.x);
                                    u = (sumRadius * invTimeStep - wLength) * unitW;
                                }
                            }

                            if (lineValid)
                            {
                                //【MassAndPush修改】
                                // 这句话的修改就是对核心算法理解的证据
                                // 论文中0.5这个hardcode有非凡的意义：当你我时间窗口交叉时（再走就要撞上了），
                                // 我们都要回避（等量谦让），每人各退一步，负责50%，50%=0.5
                                // 但是需求要求我们：质量小的要更谦让质量大的，因此通过一个简单的公式魔改这个0.5就可以了
                                //line.point = in_myVelocity + 0.5f * u;
                                line.point = in_myVelocity + (1 - (myMass / (myMass + neighorPtr->info.mass))) * u;
                                rvoLines.Add(line);
                            }
                        }


                        //以下才是令人畏惧的线性规划求解
                        var myMaxSpeed = infoPtr->maxMoveSpeed;
                        
                        var out_myVelocity = myPrefVelocity;
                        int lineFail = LP2(rvoLines.GetUnsafePtr(), rvoLines.Length, myMaxSpeed, myPrefVelocity, 0, ref out_myVelocity);
                        if (lineFail < rvoLines.Length)
                        {
                            LP3(rvoLines.GetUnsafePtr(), rvoLines.Length, numObstLines, lineFail, myMaxSpeed, ref out_myVelocity);
                        }

                        ///如果长度超过5倍的最大速度是为跳脱
                        ///RVO很忌惮单位重叠，因此需要给速度限制，不能是最大速度（别人把我挤开就应该让我快点躲开，这个很符合常理）
                        if (lengthsq(out_myVelocity) > 25 * myMaxSpeed * myMaxSpeed)
                        {
                            out_myVelocity = normalize(out_myVelocity) * 5 * myMaxSpeed;
                        }
                        iterValuesPtr->velocity = out_myVelocity
                            + iterValuesPtr->sumForce;//【MassAndPush新增】最终移动矢量=rvo+力干扰;

                    } while (false);

                    ///神圣的时刻，把velocity施加给position，写位置
                    rvoPtr->info.position = myPosition+ (iterValuesPtr->velocity) * dt;

                }
                #endregion

            }
        }

        #region 算法 - 线性规划求解(稳定库算法)
        /// <summary>
        /// RVO算法使用的直线结构
        /// 表示速度障碍物的边界线
        /// </summary>
        [BurstCompile]
        private struct RVOLine
        {
            /// <summary>直线方向向量</summary>
            public float2 dir;
            /// <summary>直线上的一点</summary>
            public float2 point;
        }

        /// <summary>
        /// Computes the determinant of a two-dimensional square matrix 
        /// with rows consisting of the specified two-dimensional vectors.
        /// </summary>
        /// <param name="a">The top row of the two-dimensional square matrix</param>
        /// <param name="b">The bottom row of the two-dimensional square matrix</param>
        /// <returns>The determinant of the two-dimensional square matrix.</returns>
        /// <summary>
        /// 计算二维向量的行列式值
        /// </summary>
        /// <param name="a">向量a</param>
        /// <param name="b">向量b</param>
        /// <returns>行列式值</returns>
        [BurstCompile]
        public static float Det(in float2 a, in float2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        /// <summary>
        /// Computes the squared distance from a line segment with the specified endpoints to a specified point.
        /// </summary>
        /// <param name="a">The first endpoint of the line segment.</param>
        /// <param name="b">The second endpoint of the line segment.</param>
        /// <param name="c">The point to which the squared distance is to be calculated.</param>
        /// <returns>The squared distance from the line segment to the point.</returns>
        /// <summary>
        /// 计算点到线段距离的平方
        /// </summary>
        /// <param name="a">线段起点</param>
        /// <param name="b">线段终点</param>
        /// <param name="c">目标点</param>
        /// <returns>距离平方值</returns>
        [BurstCompile]
        public static float DistSqPointLineSegment(in float2 a, in float2 b, in float2 c)
        {

            //TODO : inline operations instead of calling shorthands
            float2 ca = float2(c.x - a.x, c.y - a.y);
            float2 ba = float2(b.x - a.x, b.y - a.y);
            float dot = ca.x * ba.x + ca.y * ba.y;

            float r = dot / (ba.x * ba.x + ba.y * ba.y);

            if (r < 0.0f)
            {
                return ca.x * ca.x + ca.y * ca.y;
            }

            if (r > 1.0f)
            {
                float2 cb = float2(c.x - b.x, c.y - b.y);
                return cb.x * cb.x + cb.y * cb.y;
            }

            float2 d = float2(c.x - (a.x + r * ba.x), c.y - (a.y + r * ba.y));
            return d.x * d.x + d.y * d.y;

        }
        /// <summary>
        /// 判断点c在向量ab的左侧还是右侧
        /// </summary>
        /// <returns>大于0表示在左侧，小于0表示在右侧</returns>
        [BurstCompile]
        public static float LeftOf(in float2 a, in float2 b, in float2 c)
        {
            float x1 = a.x - c.x, y1 = a.y - c.y, x2 = b.x - a.x, y2 = b.y - a.y;
            return x1 * y2 - y1 * x2;
        }

        /// <summary>
        /// 一维线性规划求解
        /// 在指定直线上求解受线性约束和圆形约束的速度优化问题
        /// </summary>
        /// <param name="lines">定义线性约束的直线数组</param>
        /// <param name="lineNo">指定的约束直线索引</param>
        /// <param name="radius">圆形约束的半径</param>
        /// <param name="optVel">优化速度向量</param>
        /// <param name="dirOpt">是否优化方向(1=true)</param>
        /// <param name="result">计算结果引用</param>
        /// <returns>成功返回1，失败返回0</returns>
        [BurstCompile]
        private unsafe static byte LP1(RVOLine* lines, int lineNo, float radius, in float2 optVel, byte dirOpt, ref float2 result)
        {
            var inResult = result;
            var line = lines[lineNo];
            float2 dir = line.dir, pt = line.point;

            float dotProduct = dot(pt, dir);
            float discriminant = lengthsq(dotProduct) + lengthsq(radius) - lengthsq(pt);

            if (discriminant < 0.0f)
            {
                // Max speed circle fully invalidates line lineNo.
                return 0;
            }

            var lineA = new RVOLine();
            var dirA = float2(0, 0);
            var ptA = float2(0, 0);

            float sqrtDiscriminant = sqrt(discriminant);
            float tLeft = -dotProduct - sqrtDiscriminant;
            float tRight = -dotProduct + sqrtDiscriminant;

            for (int i = 0; i < lineNo; ++i)
            {

                lineA = lines[i]; dirA = lineA.dir; ptA = lineA.point;

                float denominator = Det(dir, dirA);
                float numerator = Det(dirA, pt - ptA);

                if (abs(denominator) <= EPSILON)
                {
                    // Lines lineNo and i are (almost) parallel.
                    if (numerator < 0.0f)
                    {
                        return 0;
                    }

                    continue;
                }

                float t = numerator / denominator;

                if (denominator >= 0.0f)
                {
                    // Line i bounds line lineNo on the right.
                    tRight = min(tRight, t);
                }
                else
                {
                    // Line i bounds line lineNo on the left.
                    tLeft = max(tLeft, t);
                }

                if (tLeft > tRight)
                {
                    return 0;
                }
            }

            if (dirOpt == 1)
            {
                // Optimize direction.
                if (dot(optVel, dir) > 0.0f)
                {
                    // Take right extreme.
                    result = pt + tRight * dir;
                }
                else
                {
                    // Take left extreme.
                    result = pt + tLeft * dir;
                }
            }
            else
            {
                // Optimize closest point.
                float t = dot(dir, (optVel - pt));

                if (t < tLeft)
                {
                    result = pt + tLeft * dir;
                }
                else if (t > tRight)
                {
                    result = pt + tRight * dir;
                }
                else
                {
                    result = pt + t * dir;
                }
            }

            return 1;
        }

        /// <summary>
        /// 二维线性规划求解
        /// 求解受线性约束和圆形约束的速度优化问题
        /// </summary>
        /// <param name="lines">定义线性约束的直线数组</param>
        /// <param name="linesCount">直线数量</param>
        /// <param name="radius">圆形约束的半径</param>
        /// <param name="optVel">优化速度向量</param>
        /// <param name="dirOpt">是否优化方向(1=true)</param>
        /// <param name="result">计算结果引用</param>
        /// <returns>失败时返回失败的行号，成功时返回总行数</returns>
        [BurstCompile]
        private unsafe static int LP2(RVOLine* lines, int linesCount, float radius, in float2 optVel, byte dirOpt, ref float2 result)
        {
            if (dirOpt == 1)
            {
                // Optimize direction. Note that the optimization velocity is of
                // unit length in this case.
                result = optVel * radius;
            }
            else if (lengthsq(optVel) > (radius * radius))
            {
                // Optimize closest point and outside circle.
                var v = normalizesafe(optVel);
                result = v * radius;
            }
            else
            {
                // Optimize closest point and inside circle.
                result = optVel;
            }

            for (int i = 0, count = linesCount; i < count; ++i)
            {
                if (Det(lines[i].dir, lines[i].point - result) > 0.0f)
                {
                    // Result does not satisfy constraint i. Compute new optimal result.
                    float2 tempResult = result;
                    if (LP1(lines, i, radius, optVel, dirOpt, ref result) == 0)
                    {
                        result = tempResult;
                        return i;
                    }
                }
            }

            return linesCount;
        }

        /// <summary>
        /// 三维线性规划求解(后备方案)
        /// 当二维线性规划失败时使用的后备算法
        /// </summary>
        /// <param name="lines">定义线性约束的直线数组</param>
        /// <param name="linesCount">直线数量</param>
        /// <param name="numObstLines">障碍物直线数量</param>
        /// <param name="beginLine">二维线性规划失败的行号</param>
        /// <param name="radius">圆形约束的半径</param>
        /// <param name="result">计算结果引用</param>
        [BurstCompile]
        private unsafe static void LP3(RVOLine* lines, int linesCount, int numObstLines, int beginLine, float radius, ref float2 result)
        {
            float distance = 0.0f;

            var lineA = new RVOLine();
            var lineB = new RVOLine();
            var dirA = float2(0, 0);
            var ptA = float2(0, 0);
            var dirB = float2(0, 0);
            var ptB = float2(0, 0);

            for (int i = beginLine, iCount = linesCount; i < iCount; ++i)
            {
                lineA = lines[i]; dirA = lineA.dir; ptA = lineA.point;

                if (Det(dirA, ptA - result) > distance)
                {
                    // Result does not satisfy constraint of line i.
                    var projLines = new UnsafeList<RVOLine>(i, Allocator.Temp, NativeArrayOptions.ClearMemory);
                    for (int ii = 0; ii < numObstLines; ++ii)
                    {
                        projLines.AddNoResize(lines[ii]);
                    }

                    for (int j = numObstLines; j < i; ++j)
                    {

                        lineB = lines[j]; dirB = lineB.dir; ptB = lineB.point;

                        var line = new RVOLine();
                        float determinant = Det(dirA, dirB);

                        if (abs(determinant) <= EPSILON)
                        {
                            // Line i and line j are parallel.
                            if (dot(dirA, dirB) > 0.0f)
                            {
                                // Line i and line j point in the same direction.
                                continue;
                            }
                            else
                            {
                                // Line i and line j point in opposite direction.
                                line.point = 0.5f * (ptA + ptB);
                            }
                        }
                        else
                        {
                            ///这里会导致跳脱，两个RVO对象基本平行的时候，line.point会很大
                            line.point = ptA + (Det(dirB, ptA - ptB) / determinant) * dirA;
                        }

                        var dir = normalizesafe(dirB - dirA);
#if RVOALGODEBUG
                        if (lengthsq(dir) < EPSILON)
                        {
                            UnityEngine.Debug.LogError($"normalize失败2.1:{dirA}");
                            UnityEngine.Debug.LogError($"normalize失败2.2:{dirB.x}");
                        }
#endif
                        line.dir = dir;
                        projLines.AddNoResize(line);
                    }

                    //if (projLines.Length != i) { UnityEngine.Debug.LogError($"炸了:{projLines.Length} {i} {numObstLines}"); }

                    float2 tempResult = result;
                    if (LP2(projLines.Ptr, projLines.Length, radius, float2(-dirA.y, dirA.x), 1, ref result) < projLines.Length)
                    {
                        // This should in principle not happen. The result is by
                        // definition already in the feasible region of this
                        // linear program. If it fails, it is due to small
                        // floating point error, and the current result is kept.
                        result = tempResult;
                    }

                    distance = Det(dirA, ptA - result);

                    //projLines.Dispose(); //Burst doesn't like this.
                }
            }
        }
        #endregion
    }
}
