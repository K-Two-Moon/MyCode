using System;
using UnityEngine;
using SPacket.SocketInstance;
using System.Threading;
using Google.ProtocolBuffers;
using Module.Log;

#pragma  warning disable

/// <summary>
/// 网络管理器状态枚举
/// </summary>
enum NETMANAGER_STATUS
{
    CONNECT_SUCESS = 0,               // 连接成功
    CONNECT_FAILED_CREATE_SOCKET_ERROR, // 创建Socket失败
    CONNECT_FAILED_CONNECT_ERROR,     // 连接服务器失败
    CONNECT_FAILED_TIME_OUT,          // 连接超时
}

/// <summary>
/// 网络通信核心管理类
/// 采用单例模式，负责所有与游戏服务器的网络通信
/// </summary>
public class NetWorkLogic
{
    // Socket错误码
    public const uint SOCKET_ERROR = 0xFFFFFFFF;
    // 数据包头大小(4字节长度 + 2字节ID)
    public const int PACKET_HEADER_SIZE = (sizeof(UInt32) + sizeof(UInt16));
    // 单个数据包最大字节数(1KB)
    public const int MAX_ONE_PACKET_BYTE_SIZE = 1024;
    // 每帧最大处理数据包数量(性能控制)
    public const int EACHFRAME_PROCESSPACKET_COUNT = 12;

    public string GetIP() { return m_strServerAddr; }
    public int GetPort() { return m_nServerPort; }

    // 连接结果回调委托
    public delegate void ConnectDelegate(bool bSuccess, string result);
    private static ConnectDelegate m_delConnect = null;

    // 连接断开回调委托
    public delegate void ConnectLostDelegate();
    private static ConnectLostDelegate m_delConnectLost = null;

    /// <summary>
    /// 获取网络管理器单例
    /// </summary>
    /// <returns>NetWorkLogic实例</returns>
    public static NetWorkLogic GetMe()
    {
        if (m_Impl == null)
        {
            m_Impl = new NetWorkLogic();
        }
        return m_Impl;
    }
    // 单例实例
    private static NetWorkLogic m_Impl = null;

    // 是否允许处理网络数据包(用于流量控制)
    private bool m_bCanProcessPacket = true;
    /// <summary>
    /// 获取或设置是否允许处理网络数据包
    /// </summary>
    public bool CanProcessPacket
    {
        get { return m_bCanProcessPacket; }
        set { m_bCanProcessPacket = value; }
    }

    //收发包流量统计
    public static int s_nReceiveCount = 0;
    public static int s_nSendCount = 0;

    private NetWorkLogic()
    {
        m_Socket = new SocketInstance();
        m_PacketFactoryManager = new PacketFactoryManagerInstance();
        m_hConnectThread = null;
        m_PacketFactoryManager.Init();
        m_SendbyteData = new byte[SocketOutputStream.DEFAULT_SOCKET_OUTPUT_BUFFER_SIZE]; //8K
        m_LenbyteData = new byte[sizeof(Int32)];
        m_PacketIDbyteData = new byte[sizeof(Int16)];
        m_MaxRevOnePacketbyteCount = MAX_ONE_PACKET_BYTE_SIZE;
        m_MaxRevOnePacketbyte = new byte[m_MaxRevOnePacketbyteCount];
        m_HeadbyteData = new byte[PACKET_HEADER_SIZE];
        m_nEachFrame_ProcessPacket_Count = EACHFRAME_PROCESSPACKET_COUNT;
    }

    /// <summary>
    /// 连接状态枚举
    /// </summary>
    public enum ConnectStatus
    {
        INVALID,       // 无效状态(初始状态)
        CONNECTING,    // 连接中
        CONNECTED,     // 已连接
        DISCONNECTED   // 已断连
    }
    /// <summary>
    /// 当前连接状态
    /// </summary>
    protected ConnectStatus m_connectStatus = ConnectStatus.INVALID;
    /// <summary>
    /// 连接是否完成标志
    /// </summary>
    private bool m_bConnectFinish = false;
    /// <summary>
    /// 连接结果描述
    /// </summary>
    private string m_strConnectResult = "";

    /// <summary>
    /// 检查是否已断开连接
    /// </summary>
    /// <returns>true表示已断开</returns>
    public bool IsDisconnected() { return m_connectStatus == ConnectStatus.DISCONNECTED; }

    /// <summary>
    /// 设置状态为连接中
    /// </summary>
    public void WaitConnected() { m_connectStatus = ConnectStatus.CONNECTING; }

    /// <summary>
    /// 获取当前连接状态
    /// </summary>
    /// <returns>当前连接状态</returns>
    public ConnectStatus GetConnectStautus() { return m_connectStatus; }

    /// <summary>
    /// 设置连接结果回调委托
    /// </summary>
    /// <param name="delConnectFun">连接结果回调函数</param>
    public static void SetConcnectDelegate(ConnectDelegate delConnectFun)
    {
        m_delConnect = delConnectFun;
    }

    /// <summary>
    /// 设置连接断开回调委托
    /// </summary>
    /// <param name="delFun">连接断开回调函数</param>
    public static void SetConnectLostDelegate(ConnectLostDelegate delFun)
    {
        m_delConnectLost = delFun;
    }

    /// <summary>
    /// 主更新循环
    /// 每帧调用，处理网络连接状态更新和数据包处理
    /// </summary>
    public void Update()
    {
        // 处理连接完成回调
        if (m_bConnectFinish)
        {
            m_bConnectFinish = false;
            if (null != m_delConnect)
            {
                // 连接成功回调
                m_delConnect(m_connectStatus == ConnectStatus.CONNECTED, m_strConnectResult);
            }
        }

        // 处理网络数据包
        WaitPacket();
    }

    void Reset()
    {
        DisconnectServer();
    }

    void ConnectLost()
    {
        m_connectStatus = ConnectStatus.DISCONNECTED;
        if (null != m_delConnectLost) m_delConnectLost();
    }
    ~NetWorkLogic()
    {
        DisconnectServer();
    }

    #region NetWork Process

    public bool IsCryptoPacket(UInt16 nPacketID)
    {
        return (nPacketID != (UInt16)MessageID.PACKET_CG_LOGIN &&
                nPacketID != (UInt16)MessageID.PACKET_GC_LOGIN_RET &&
                nPacketID != (UInt16)MessageID.PACKET_CG_CONNECTED_HEARTBEAT &&
                nPacketID != (UInt16)MessageID.PACKET_GC_CONNECTED_HEARTBEAT);
    }

    /// <summary>
    /// 等待并处理网络数据包
    /// 主网络处理循环，负责调用输入输出处理和数据包解析
    /// </summary>
    void WaitPacket()
    {
        try
        {
            // 检查Socket有效性
            if (!m_Socket.IsValid)
            {
                return;
            }
            // 检查连接状态
            if (!m_Socket.IsConnected)
            {
                return;
            }
            // 处理网络输入
            if (!ProcessInput())
            {
                return;
            }
            // 处理网络输出
            if (!ProcessOutput())
            {
                return;
            }

            // 处理接收到的数据包
            ProcessPacket();
        }
        catch (System.Exception ex)
        {
            LogModule.ErrorLog(ex.Message);
        }
    }

    /// <summary>
    /// 处理网络输入数据
    /// 从Socket读取数据到输入缓冲区
    /// </summary>
    /// <returns>处理是否成功</returns>
    bool ProcessInput()
    {
        if (m_SocketInputStream == null)
        {
            return false;
        }
        // 检查是否可以接收数据
        if (m_Socket.IsCanReceive() == false)
        {
            return true;
        }

        // 读取网络数据
        uint nSizeBefore = m_SocketInputStream.Length();
        uint ret = m_SocketInputStream.Fill();
        uint nSizeAfter = m_SocketInputStream.Length();

        // 错误处理
        if (ret == SOCKET_ERROR)
        {
            LogModule.WarningLog("send packet fail");
            m_Socket.close();
            ConnectLost();
            return false;
        }

        // 收包流量统计
        if (nSizeAfter > nSizeBefore)
        {
            if (NetWorkLogic.s_nReceiveCount < 0)
            {
                NetWorkLogic.s_nReceiveCount = 0;
            }
            NetWorkLogic.s_nReceiveCount += (int)(nSizeAfter - nSizeBefore);
        }
        return true;
    }

    /// <summary>
    /// 处理网络输出数据
    /// 将输出缓冲区数据写入Socket
    /// </summary>
    /// <returns>处理是否成功</returns>
    bool ProcessOutput()
    {
        if (m_SocketOutputStream == null)
        {
            return false;
        }
        // 检查是否可以发送数据
        if (m_Socket.IsCanSend() == false)
        {
            return true;
        }

        // 刷新输出缓冲区
        uint ret = m_SocketOutputStream.Flush();
        if (ret == SOCKET_ERROR)
        {
            LogModule.WarningLog("send packet fail");
            m_Socket.close();
            ConnectLost();
            return false;
        }

        return true;
    }

    /// <summary>
    /// 处理接收到的数据包
    /// 从输入缓冲区解析数据包并执行对应的处理逻辑
    /// </summary>
    void ProcessPacket()
    {
        if (m_SocketInputStream == null)
        {
            return;
        }

        // 每帧处理数据包数量控制
        int nProcessPacketCount = m_nEachFrame_ProcessPacket_Count;

        Int32 packetSize;
        Int16 messageid;
        Ipacket pPacket = null;

        // 循环处理数据包，直到处理完指定数量或缓冲区无数据
        while (m_bCanProcessPacket && (nProcessPacketCount--) > 0)
        {
            // 读取数据包头
            Array.Clear(m_HeadbyteData, 0, PACKET_HEADER_SIZE);
            if (!m_SocketInputStream.Peek(m_HeadbyteData, PACKET_HEADER_SIZE))
            {
                break;
            }

            // 解析数据包大小和消息ID
            packetSize = BitConverter.ToInt32(m_HeadbyteData, 0);
            packetSize = System.Net.IPAddress.NetworkToHostOrder(packetSize) + 4;

            messageid = BitConverter.ToInt16(m_HeadbyteData, sizeof(UInt32));
            messageid = System.Net.IPAddress.NetworkToHostOrder(messageid);

            // 检查缓冲区是否有完整数据包
            if (m_SocketInputStream.Length() < packetSize)
            {
                break;
            }

            try
            {
                // 调整接收缓冲区大小
                if (m_MaxRevOnePacketbyteCount < packetSize)
                {
                    m_MaxRevOnePacketbyte = new byte[packetSize];
                    m_MaxRevOnePacketbyteCount = packetSize;
                }
                Array.Clear(m_MaxRevOnePacketbyte, 0, m_MaxRevOnePacketbyteCount);

                // 跳过包头，读取包体数据
                bool bRet = m_SocketInputStream.Skip(PACKET_HEADER_SIZE);
                if (bRet == false)
                {
                    string errorLog = string.Format("Can not Create Packet MessageID({0},packetSize{1})", messageid, packetSize);
                    throw PacketException.PacketReadError(errorLog);
                }
                m_SocketInputStream.Read(m_MaxRevOnePacketbyte, (uint)(packetSize - PACKET_HEADER_SIZE));

                // 获取对应的数据包处理器
                pPacket = m_PacketFactoryManager.GetPacketHandler((MessageID)messageid);
                NGUIDebug.Log("ReceivePacket:" + ((MessageID)messageid).ToString());
                NGUIDebug.Log("PacketSize:" + (packetSize));

                // 检查处理器是否有效
                if (pPacket == null)
                {
                    string errorLog = string.Format("Can not Create Packet MessageID({0},buff{1})", messageid, LogModule.ByteToString(m_MaxRevOnePacketbyte, 0, m_MaxRevOnePacketbyteCount));
                    throw PacketException.PacketCreateError(errorLog);
                }

                // 创建并解析数据包
                PacketDistributed realPacket = PacketDistributed.CreatePacket((MessageID)messageid);
                if (realPacket == null)
                {
                    string errorLog = string.Format("Can not Create Inner Packet Data MessageID({0},buff{1})", messageid, LogModule.ByteToString(m_MaxRevOnePacketbyte, 0, m_MaxRevOnePacketbyteCount));
                    throw PacketException.PacketCreateError(errorLog);
                }

                // 合并包体数据
                PacketDistributed instancePacket = realPacket.ParseFrom(m_MaxRevOnePacketbyte, packetSize - PACKET_HEADER_SIZE);
                if (instancePacket == null)
                {
                    string errorLog = string.Format("Can not Merged Inner Packet Data MessageID({0},buff{1})", messageid, LogModule.ByteToString(m_MaxRevOnePacketbyte, 0, m_MaxRevOnePacketbyteCount));
                    throw PacketException.PacketCreateError(errorLog);
                }

                // 执行数据包处理逻辑
                uint result = pPacket.Execute(instancePacket);

                // 根据处理结果决定是否移除处理器
                if ((PACKET_EXE)result != PACKET_EXE.PACKET_EXE_NOTREMOVE)
                {
                    m_PacketFactoryManager.RemovePacket(pPacket);
                }
                else if ((PACKET_EXE)result == PACKET_EXE.PACKET_EXE_ERROR)
                {
                    string errorLog = string.Format("Execute Packet error!!! MessageID({0},buff{1})", messageid, LogModule.ByteToString(m_MaxRevOnePacketbyte, 0, m_MaxRevOnePacketbyteCount));
                    throw PacketException.PacketExecuteError(errorLog);
                }
            }
            catch (PacketException ex)
            {
                LogModule.ErrorLog(ex.ToString());
            }
            catch (System.Exception ex)
            {
                LogModule.ErrorLog(ex.ToString());
            }
        }

        // 动态调整每帧处理数据包数量
        if (nProcessPacketCount >= 0)
        {
            m_nEachFrame_ProcessPacket_Count = EACHFRAME_PROCESSPACKET_COUNT;
        }
        else
        {
            m_nEachFrame_ProcessPacket_Count += 4;
        }
    }

    /// <summary>
    /// 发送数据包到服务器
    /// </summary>
    /// <param name="pPacket">要发送的数据包</param>
    public void SendPacket(PacketDistributed pPacket)
    {
        if (pPacket == null)
        {
            return;
        }
        NGUIDebug.Log("SendPacket:" + pPacket.ToString());

        // 检查连接状态
        if (m_connectStatus != ConnectStatus.CONNECTED)
        {
            if (m_connectStatus == ConnectStatus.DISCONNECTED)
            {
                // 再次询问断线重连情况
                ConnectLost();
            }
            return;
        }

        if (m_Socket.IsValid)
        {
            // 检查数据包是否已初始化
            if (!pPacket.IsInitialized())
            {
                throw InvalidProtocolBufferException.ErrorMsg("Request data have not set");
            }

            // 获取数据包序列化后的大小
            int nValidbyteSize = pPacket.SerializedSize();
            if (nValidbyteSize <= 0)
            {
                return;
            }

            // 准备发送缓冲区
            int nClearCount = nValidbyteSize + 128;
            if (nClearCount > (int)SocketOutputStream.DEFAULT_SOCKET_OUTPUT_BUFFER_SIZE)
            {
                nClearCount = (int)SocketOutputStream.DEFAULT_SOCKET_OUTPUT_BUFFER_SIZE;
            }
            Array.Clear(m_SendbyteData, 0, nClearCount);

            // 序列化数据包
            CodedOutputStream output = CodedOutputStream.CreateInstance(m_SendbyteData, 0, nValidbyteSize);
            pPacket.WriteTo(output);
            output.CheckNoSpaceLeft();

            // 准备包头数据(长度和消息ID)
            Int32 nlen = nValidbyteSize + NetWorkLogic.PACKET_HEADER_SIZE - 4;
            Int32 netnlen = System.Net.IPAddress.HostToNetworkOrder(nlen);
            Int16 messageid = System.Net.IPAddress.HostToNetworkOrder((Int16)pPacket.GetPacketID());

            Array.Clear(m_LenbyteData, 0, sizeof(Int32));
            Array.Clear(m_PacketIDbyteData, 0, sizeof(Int16));

            // 填充长度字段(小端序)
            m_LenbyteData[0] = (byte)(netnlen);
            m_LenbyteData[1] = (byte)(netnlen >> 8);
            m_LenbyteData[2] = (byte)(netnlen >> 16);
            m_LenbyteData[3] = (byte)(netnlen >> 24);

            // 填充消息ID字段
            m_PacketIDbyteData[0] = (byte)(messageid);
            m_PacketIDbyteData[1] = (byte)(messageid >> 8);

            // 写入输出缓冲区
            uint nSizeBefore = m_SocketOutputStream.Length();
            m_SocketOutputStream.Write(m_LenbyteData, sizeof(Int32));
            m_SocketOutputStream.Write(m_PacketIDbyteData, sizeof(Int16));
            m_SocketOutputStream.Write(m_SendbyteData, (uint)nValidbyteSize);
            uint nSizeAfter = m_SocketOutputStream.Length();

            // 更新发包统计
            if (nSizeAfter > nSizeBefore)
            {
                if (NetWorkLogic.s_nSendCount < 0)
                {
                    NetWorkLogic.s_nSendCount = 0;
                }
                NetWorkLogic.s_nSendCount += (int)(nSizeAfter - nSizeBefore);
            }
        }
        else
        {
            ConnectLost();
        }
    }

    #endregion

    #region Common Service
    /// <summary>
    /// 连接到指定服务器
    /// </summary>
    /// <param name="szServerAddr">服务器地址</param>
    /// <param name="nServerPort">服务器端口</param>
    /// <param name="nSleepTime">连接失败后重试间隔(毫秒)</param>
    public void ConnectToServer(string szServerAddr, int nServerPort, int nSleepTime)
    {
        // 如果正在连接中，则直接返回
        if (m_connectStatus == ConnectStatus.CONNECTING)
        {
            return;
        }

        // 保存连接参数
        m_strServerAddr = szServerAddr;
        m_nServerPort = nServerPort;
        m_nConnectSleep = nSleepTime;

        // 启动连接线程
        m_hConnectThread = new Thread(new ParameterizedThreadStart(_ConnectThread));
        m_timeConnectBegin = Time.time;
        m_hConnectThread.Start(this);
    }

    /// <summary>
    /// 重新连接到服务器
    /// </summary>
    public void ReConnectToServer()
    {
        // 如果正在连接中，则直接返回
        if (m_connectStatus == ConnectStatus.CONNECTING)
        {
            return;
        }

        // 清理输入输出流
        if (m_SocketInputStream != null)
        {
            m_SocketInputStream.CleanUp();
        }

        if (m_SocketOutputStream != null)
        {
            m_SocketOutputStream.CleanUp();
        }

        // 启动连接线程
        m_hConnectThread = new Thread(new ParameterizedThreadStart(_ConnectThread));
        m_timeConnectBegin = Time.time;
        m_hConnectThread.Start(this);
    }

    /// <summary>
    /// 断开与服务器的连接
    /// </summary>
    public void DisconnectServer()
    {
        if (m_strServerAddr == null || m_strServerAddr.Length == 0) return;

        // 关闭Socket并更新状态
        m_Socket.close();
        m_connectStatus = ConnectStatus.DISCONNECTED;
    }


    #endregion

    /// <summary>
    /// 服务器地址
    /// </summary>
    string m_strServerAddr;
    /// <summary>
    /// 服务器端口
    /// </summary>
    int m_nServerPort;
    /// <summary>
    /// 连接重试间隔时间(毫秒)
    /// </summary>
    int m_nConnectSleep;
    /// <summary>
    /// Socket实例
    /// </summary>
    SocketInstance m_Socket;
    /// <summary>
    /// 数据包工厂管理器
    /// </summary>
    PacketFactoryManagerInstance m_PacketFactoryManager;
    /// <summary>
    /// Socket输入流
    /// </summary>
    SocketInputStream m_SocketInputStream;
    /// <summary>
    /// Socket输出流
    /// </summary>
    SocketOutputStream m_SocketOutputStream;
    /// <summary>
    /// 数据包索引(用于加密)
    /// </summary>
    Byte m_nPacketIndex = 123;

    // 发包缓存区
    private byte[] m_SendbyteData;
    // 数据包长度缓存
    private byte[] m_LenbyteData;
    // 数据包ID缓存
    private byte[] m_PacketIDbyteData;

    // 收包缓存区
    private int m_MaxRevOnePacketbyteCount;
    private byte[] m_MaxRevOnePacketbyte;

    // 数据包头缓存
    private byte[] m_HeadbyteData;

    // 每帧处理数据包数量上限
    private int m_nEachFrame_ProcessPacket_Count;

    //////////////////////////////////////////////////////////////////////////
    #region Thread For Connect

    /// <summary>
    /// 连接线程主方法
    /// 负责实际建立与服务器的连接
    /// </summary>
    public void ConnectThread()
    {
        m_connectStatus = ConnectStatus.CONNECTING;
        while (true)
        {
            // 确保Socket已关闭
            m_Socket.close();

            // 尝试连接服务器
            Console.WriteLine("connect:" + m_strServerAddr);
            m_strConnectResult = m_Socket.connect(m_strServerAddr, m_nServerPort);

            // 连接成功处理
            if (m_strConnectResult.Length == 0 && m_Socket.IsValid)
            {
                // 初始化输入输出流
                m_SocketInputStream = new SocketInputStream(m_Socket);
                m_SocketOutputStream = new SocketOutputStream(m_Socket);
                m_connectStatus = ConnectStatus.CONNECTED;
                break;
            }
            else
            {
                // 连接失败记录日志
                LogModule.WarningLog(m_strConnectResult);
            }

            // 连接失败后关闭Socket并等待重试
            m_Socket.close();
            Thread.Sleep(m_nConnectSleep);
            m_connectStatus = ConnectStatus.DISCONNECTED;
            break;
        }
        // 标记连接完成
        m_bConnectFinish = true;
    }

    /// <summary>
    /// 连接线程入口方法
    /// </summary>
    /// <param name="me">NetWorkLogic实例</param>
    protected static void _ConnectThread(object me)
    {
        NetWorkLogic rMe = me as NetWorkLogic;
        rMe.ConnectThread();
    }

    // 连接线程句柄
    Thread m_hConnectThread = null;
    // 连接开始时间
    float m_timeConnectBegin;
    #endregion

}
