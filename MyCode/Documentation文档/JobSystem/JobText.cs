using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

// Burst 包 提供了一种编译器，可以将 C# 代码转换为底层代码，以提高性能。
// Collections 包 提供了对数组、列表、字典等数据结构的支持，可以让我们在游戏开发中更方便地使用这些数据结构。
//Mathematics 包 提供了 Unity的c# SIMD数学库提供了类似shader语法的矢量类型和数学函数。

public class JobText : MonoBehaviour
{
    [Header("异步单线程执行")]
    [SerializeField] bool isJob1;
    [Header("同步主线程执行")]
    [SerializeField] bool isJob2_1;
    [Header("异步单线程执行")]
    [SerializeField] bool isJob2_2;
    [Header("多线程并行执行")]
    [SerializeField] bool isJob2_3;
    [Header("多线程并行执行")]
    [SerializeField] bool isJob3;
    void Start()
    {
        if (isJob1)
            Job1();
        if (isJob2_1)
            Job2_1();
        if (isJob2_2)
            Job2_2();
        if (isJob2_3)
            Job2_3();
        if (isJob3)
            Job3();
    }


    /// <summary>
    ///  IJob调度方法：异步工作执行，不会阻塞主线程
    /// </summary>
    void Job1()
    {
        // 创建一个包含 10 个元素的数组
        NativeArray<float> numbers = new NativeArray<float>(100, Allocator.TempJob, NativeArrayOptions.ClearMemory);
        NativeArray<float> results = new NativeArray<float>(100, Allocator.TempJob);

        // 初始化数组
        for (int i = 0; i < numbers.Length; i++)
        {
            numbers[i] = i + 1; // 填充数组 1, 2, 3, ..., 10
        }

        // 创建 Job
        JobMethod1 job = new JobMethod1
        {
            numbers = numbers,
            results = results
        };

        // 调度 Job
        JobHandle handle = job.Schedule();

        // 等待 Job 完成
        handle.Complete();

        // 输出结果
        for (int i = 0; i < results.Length; i++)
        {
            Debug.Log($" 平方 {numbers[i]} is {results[i]}");
        }

        // 释放 NativeArray
        numbers.Dispose();
        results.Dispose();
    }
    /// <summary>
    /// IJobFor调度方法1：同步执行，会阻塞主线程
    /// </summary>
    void Job2_1()
    {
        // 创建一个包含 10 个元素的数组
        NativeArray<float> numbers = new NativeArray<float>(10000, Allocator.TempJob);
        NativeArray<float> results = new NativeArray<float>(10000, Allocator.TempJob);

        // 初始化数组
        for (int i = 0; i < numbers.Length; i++)
        {
            numbers[i] = i + 1; // 填充数组 1, 2, 3, ..., 10
        }

        // 创建 Job
        JobMethod2 job = new JobMethod2
        {
            numbers = numbers,
            results = results
        };

        // 调度 Job
        job.Run(numbers.Length); //同步执行，会阻塞主线程

        // 输出结果
        for (int i = 0; i < results.Length; i++)
        {
            Debug.Log($"Square of {numbers[i]} is {results[i]}");
        }

        // 释放内存
        numbers.Dispose();
        results.Dispose();
    }
    /// <summary>
    /// IJobFor调度方法2：异步工作执行，不会阻塞主线程
    /// </summary>
    private void Job2_2()
    {
        // 创建一个包含 10 个元素的数组
        NativeArray<float> numbers = new NativeArray<float>(10000, Allocator.TempJob);
        NativeArray<float> results = new NativeArray<float>(10000, Allocator.TempJob);

        // 初始化数组
        for (int i = 0; i < numbers.Length; i++)
        {
            numbers[i] = i + 1; // 填充数组 1, 2, 3, ..., 10
        }

        // 创建 Job
        JobMethod2 job = new JobMethod2
        {
            numbers = numbers,
            results = results
        };

        // 调度 Job
        JobHandle scheduleJobHandle = job.Schedule(numbers.Length, default);

        // 等待 Job 完成
        scheduleJobHandle.Complete();
        // 输出结果
        for (int i = 0; i < results.Length; i++)
        {
            Debug.Log($"Square of {numbers[i]} is {results[i]}");
        }

        // 释放内存
        numbers.Dispose();
        results.Dispose();
    }
    /// <summary>
    /// IJobFor调度方法3：多线程并行执行，不会阻塞主线程
    /// </summary>
    void Job2_3()
    {
        // 创建一个包含 10 个元素的数组
        NativeArray<float> numbers = new NativeArray<float>(10000, Allocator.TempJob);
        NativeArray<float> results = new NativeArray<float>(10000, Allocator.TempJob);

        // 初始化数组
        for (int i = 0; i < numbers.Length; i++)
        {
            numbers[i] = i + 1; // 填充数组 1, 2, 3, ..., 10
        }

        // 创建 Job
        JobMethod2 job = new JobMethod2
        {
            numbers = numbers,
            results = results
        };

        // 调度 Job
        JobHandle scheduleParallelJobHandle = job.ScheduleParallel(numbers.Length, 1000, default); // 并行执行，不会阻塞主线程

        // 等待 Job 完成
        scheduleParallelJobHandle.Complete();
        // 输出结果
        for (int i = 0; i < results.Length; i++)
        {
            Debug.Log($"Square of {numbers[i]} is {results[i]}");
        }

        // 释放内存
        numbers.Dispose();
        results.Dispose();
    }
    /// <summary>
    /// IJobParallelFor调度方法：多线程并行执行，不会阻塞主线程
    /// </summary>
    private void Job3()
    {
        NativeArray<float> numbers = new NativeArray<float>(100000, Allocator.TempJob);
        NativeArray<float> results = new NativeArray<float>(100000, Allocator.TempJob);

        for (int i = 0; i < numbers.Length; i++)
        {
            numbers[i] = i + 1;
        }

        JobMethod3 job = new JobMethod3
        {
            numbers = numbers,
            results = results
        };

        // 调度并行作业 参数：总任务数，每批任务的大小  作用：将任务分成多个批次，并行执行
        JobHandle handle = job.Schedule(numbers.Length, 100);

        // 判断 Job 是否完成
        while (handle.IsCompleted == false)
        {
            Debug.Log("Job is running...");
        }

        handle.Complete();

        for (int i = 0; i < results.Length; i++)
        {
            Debug.Log($"Square of {numbers[i]} is {results[i]}");
        }

        numbers.Dispose();
        results.Dispose();
    }
}
/// <summary>
/// 定义单个作业
/// </summary>

[BurstCompile]
public struct JobMethod1 : IJob
{
    public NativeArray<float> numbers; // 输入数组
    public NativeArray<float> results; // 输出数组
    public void Execute()
    {
        for (int i = 0; i < numbers.Length; i++)
        {
            results[i] = numbers[i] * numbers[i]; // 计算平方
        }
    }
}

[BurstCompile]
public struct JobMethod2 : IJobFor
{
    public NativeArray<float> numbers;
    public NativeArray<float> results;

    public void Execute(int index)
    {
        results[index] = numbers[index] * numbers[index];
    }
}

/// <summary>
/// 定义并行作业
/// </summary>

[BurstCompile]
public struct JobMethod3 : IJobParallelFor
{
    public NativeArray<float> numbers;
    public NativeArray<float> results;
    public void Execute(int index)
    {
        results[index] = numbers[index] * numbers[index];
    }
}