using 暗潮航线.规则;

namespace 暗潮航线.房间.测试;

public class 房间执行器测试
{
    private static readonly Guid 玩家甲 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid 玩家乙 = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task 并发抢同一座位只有一人成功且拒绝不会终止队列()
    {
        await using var 执行器 = new 房间执行器(Guid.NewGuid(), 8);
        var 开始信号 = 新建信号();
        var 玩家们 = Enumerable.Range(0, 16).Select(_ => Guid.NewGuid()).ToArray();
        var 请求们 = 玩家们.Select(async 玩家 =>
        {
            await 开始信号.Task;
            try
            {
                await 提交入座(执行器, 玩家, 6);
                return true;
            }
            catch (规则拒绝异常 错误) when (错误.原因 == 拒绝原因.座位已有玩家)
            {
                return false;
            }
        }).ToArray();

        开始信号.SetResult(true);
        var 结果们 = await Task.WhenAll(请求们).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, 结果们.Count(成功 => 成功));
        for (int 位置 = 0; 位置 < 玩家们.Length; 位置++)
        {
            Assert.Equal<int?>(结果们[位置] ? 6 : null, await 执行器.查找座位异步(玩家们[位置]));
        }
        await 提交入座(执行器, 玩家甲, 0);
        Assert.Equal<int?>(0, await 执行器.查找座位异步(玩家甲));
    }

    [Fact]
    public async Task 已入队请求按顺序执行且查询不会越过前面的修改()
    {
        var 信号 = 新建信号();
        await using var 执行器 = new 房间执行器(new 房间(Guid.NewGuid(), 8), 4, 信号.Task);
        try
        {
            var 入座 = 提交入座(执行器, 玩家甲, 3);
            var 离座 = 提交离座(执行器, 玩家甲);
            var 重新入座 = 提交入座(执行器, 玩家乙, 3);
            var 查询 = 执行器.查找座位异步(玩家乙);
            Assert.False(查询.IsCompleted);
            信号.SetResult(true);

            await Task.WhenAll(入座, 离座, 重新入座);
            Assert.Equal<int?>(3, await 查询);
            Assert.Null(await 执行器.查找座位异步(玩家甲));
        }
        finally
        {
            信号.TrySetResult(true);
        }
    }

    [Fact]
    public async Task 满队列立即拒绝且拒绝的请求不会偷偷执行()
    {
        var 信号 = 新建信号();
        await using var 执行器 = new 房间执行器(new 房间(Guid.NewGuid(), 8), 1, 信号.Task);
        try
        {
            var 已接收 = 提交入座(执行器, 玩家甲, 0);
            var 超出容量 = 提交入座(执行器, 玩家乙, 1);

            Assert.True(超出容量.IsCompleted);
            await Assert.ThrowsAsync<房间繁忙异常>(() => 超出容量);
            Assert.False(已接收.IsCompleted);
            信号.SetResult(true);
            await 已接收;

            Assert.Null(await 执行器.查找座位异步(玩家乙));
            await 提交入座(执行器, 玩家乙, 1);
            Assert.Equal<int?>(1, await 执行器.查找座位异步(玩家乙));
        }
        finally
        {
            信号.TrySetResult(true);
        }
    }

    [Fact]
    public async Task 命令创建后修改原数组不影响排队中的发牌()
    {
        var 信号 = 新建信号();
        await using var 执行器 = new 房间执行器(new 房间(Guid.NewGuid(), 8), 4, 信号.Task);
        try
        {
            var 甲入座 = 提交入座(执行器, 玩家甲, 6);
            var 乙入座 = 提交入座(执行器, 玩家乙, 0);
            var 牌序 = 牌库.创建标准牌库().ToArray();
            int[] 顺序 = { 6, 0 };
            var 发牌 = 提交发牌(执行器, 牌序, 顺序);
            var 查看 = 执行器.获取本人视图异步(玩家甲);

            Array.Reverse(牌序);
            Array.Reverse(顺序);
            信号.SetResult(true);
            await Task.WhenAll(甲入座, 乙入座, 发牌);
            var 视图 = await 查看;

            Assert.Equal(6, 视图.本人座位);
            Assert.Equal(new[] { new 牌(花色.红, 1), new 牌(花色.红, 3) }, 视图.本人底牌);
        }
        finally
        {
            信号.TrySetResult(true);
        }
    }

    [Fact]
    public async Task 非法请求拒绝后仍可处理正常发牌与查看()
    {
        await using var 执行器 = new 房间执行器(Guid.NewGuid(), 8);
        await Assert.ThrowsAsync<规则拒绝异常>(() => 提交入座(执行器, 玩家甲, 8));
        await 提交入座(执行器, 玩家甲, 6);
        await 提交入座(执行器, 玩家乙, 0);
        await Assert.ThrowsAsync<规则拒绝异常>(() => 提交发牌(执行器, 牌库.创建标准牌库(), new[] { 6, 7 }));
        await 提交发牌(执行器, 牌库.创建标准牌库(), new[] { 6, 0 });
        await Assert.ThrowsAsync<规则拒绝异常>(() => 执行器.获取本人视图异步(Guid.NewGuid()));
        Assert.Equal(2, (await 执行器.获取本人视图异步(玩家甲)).本人底牌.Count);
    }

    [Fact]
    public async Task 关闭后拒绝新请求但先处理完所有已接收请求()
    {
        var 信号 = 新建信号();
        await using var 执行器 = new 房间执行器(new 房间(Guid.NewGuid(), 8), 2, 信号.Task);
        try
        {
            var 入座 = 提交入座(执行器, 玩家甲, 6);
            var 查询 = 执行器.查找座位异步(玩家甲);
            var 关闭 = 执行器.DisposeAsync().AsTask();

            Assert.False(关闭.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => 提交入座(执行器, 玩家乙, 0));
            信号.SetResult(true);
            await 关闭;
            await 入座;
            Assert.Equal<int?>(6, await 查询);
            await 执行器.DisposeAsync();
            Assert.Null(执行器.故障原因);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => 执行器.查找座位异步(玩家甲));
        }
        finally
        {
            信号.TrySetResult(true);
        }
    }

    [Fact]
    public async Task 多个调用方与关闭竞争时所有请求都有明确结果()
    {
        var 执行器 = new 房间执行器(Guid.NewGuid(), 8);
        var 信号 = 新建信号();
        var 请求们 = Enumerable.Range(0, 16).Select(async _ =>
        {
            await 信号.Task;
            try
            {
                Assert.Null(await 执行器.查找座位异步(玩家甲));
            }
            catch (ObjectDisposedException)
            {
                // 与停止接收竞争时，也允许明确拒绝，但不能永久等待。
            }
        }).ToArray();
        var 关闭任务 = Task.Run(async () =>
        {
            await 信号.Task;
            await 执行器.DisposeAsync();
        });
        try
        {
            信号.SetResult(true);
            await Task.WhenAll(请求们.Append(关闭任务)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await 执行器.DisposeAsync();
        }
    }

    [Fact]
    public async Task 一个房间暂停消费不会阻止另一个房间处理请求()
    {
        var 信号 = 新建信号();
        await using var 第一间 = new 房间执行器(new 房间(Guid.NewGuid(), 8), 1, 信号.Task);
        await using var 第二间 = new 房间执行器(Guid.NewGuid(), 8);
        try
        {
            var 等待请求 = 提交入座(第一间, 玩家甲, 6);
            await 提交入座(第二间, 玩家乙, 0).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal<int?>(0, await 第二间.查找座位异步(玩家乙));
            Assert.False(等待请求.IsCompleted);
            信号.SetResult(true);
            await 等待请求;
            Assert.Null(await 第一间.查找座位异步(玩家乙));
        }
        finally
        {
            信号.TrySetResult(true);
        }
    }

    [Fact]
    public async Task 消费循环意外故障时排队请求全部结束且拒绝后续请求()
    {
        var 信号 = 新建信号();
        var 执行器 = new 房间执行器(new 房间(Guid.NewGuid(), 8), 2, 信号.Task);
        var 第一条 = 提交入座(执行器, 玩家甲, 0);
        var 第二条 = 执行器.查找座位异步(玩家甲);
        信号.SetException(new ApplicationException("测试模拟消费循环故障。"));

        await 执行器.已停止.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<ApplicationException>(执行器.故障原因);
        await Assert.ThrowsAsync<房间故障异常>(() => 第一条);
        await Assert.ThrowsAsync<房间故障异常>(() => 第二条);
        var 后续拒绝 = await Assert.ThrowsAsync<房间故障异常>(() => 提交入座(执行器, 玩家乙, 1));
        Assert.Same(执行器.故障原因, 后续拒绝.InnerException);
        // 故障已通过属性公开，关闭时不再重复抛出。
        await 执行器.DisposeAsync();
    }

    [Fact]
    public async Task 并发重发同一命令只生效一次且都拿到同一回执()
    {
        await using var 执行器 = new 房间执行器(Guid.NewGuid(), 8);
        var 开始信号 = 新建信号();
        var 命令 = new 入座命令(Guid.NewGuid(), 玩家甲, 2);
        // 模拟多条连接同时重发；队列把它们排成先后，第一条执行，其余命中记录。
        var 重发们 = Enumerable.Range(0, 8).Select(async _ =>
        {
            await 开始信号.Task;
            return await 执行器.执行异步(命令);
        }).ToArray();

        开始信号.SetResult(true);
        var 回执们 = await Task.WhenAll(重发们).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(回执们.Distinct());
        var 概况 = await 执行器.获取概况异步();
        Assert.Equal(1, 概况.状态版本);
        Assert.True(概况.座位有人[2]);
    }

    [Fact]
    public async Task 查询额度满时命令仍可入队()
    {
        var 信号 = 新建信号();
        await using var 执行器 = new 房间执行器(new 房间(Guid.NewGuid(), 8), 2, 信号.Task, 查询队列容量: 1);
        try
        {
            var 第一条查询 = 执行器.查找座位异步(玩家甲);
            await Assert.ThrowsAsync<房间繁忙异常>(() => 执行器.获取概况异步());

            // 刷新视图挤满了查询额度，但入座命令有自己的额度。
            var 命令 = 提交入座(执行器, 玩家甲, 0);
            Assert.False(命令.IsCompleted);

            信号.SetResult(true);
            Assert.Null(await 第一条查询);
            await 命令;
            Assert.Equal<int?>(0, await 执行器.查找座位异步(玩家甲));
        }
        finally
        {
            信号.TrySetResult(true);
        }
    }

    [Fact]
    public async Task 命令额度满时查询仍可入队且处理后归还额度()
    {
        var 信号 = 新建信号();
        await using var 执行器 = new 房间执行器(new 房间(Guid.NewGuid(), 8), 1, 信号.Task);
        try
        {
            var 第一条命令 = 提交入座(执行器, 玩家甲, 0);
            await Assert.ThrowsAsync<房间繁忙异常>(() => 提交入座(执行器, 玩家乙, 1));

            var 查询 = 执行器.查找座位异步(玩家甲);
            Assert.False(查询.IsCompleted);

            信号.SetResult(true);
            await 第一条命令;
            // 查询排在命令之后，所以能看到它的结果。
            Assert.Equal<int?>(0, await 查询);
            // 额度已归还，后续命令可以再次进入。
            await 提交入座(执行器, 玩家乙, 1);
        }
        finally
        {
            信号.TrySetResult(true);
        }
    }

    [Fact]
    public async Task 概况区分空座与已入座未参与()
    {
        await using var 执行器 = new 房间执行器(Guid.NewGuid(), 4);
        await 提交入座(执行器, 玩家甲, 0);
        await 提交入座(执行器, 玩家乙, 1);
        await 提交入座(执行器, Guid.NewGuid(), 2);
        await 提交发牌(执行器, 牌库.创建标准牌库(), new[] { 1, 0 });

        var 概况 = await 执行器.获取概况异步();

        Assert.Equal(房间阶段.已发底牌, 概况.阶段);
        Assert.Equal(4, 概况.状态版本);
        Assert.Equal(new[] { true, true, true, false }, 概况.座位有人);
        Assert.Equal(new[] { 1, 0 }, 概况.本局参与座位顺序);
    }

    [Fact]
    public async Task 拒绝空命令()
    {
        await using var 执行器 = new 房间执行器(Guid.NewGuid(), 8);
        Assert.Throws<ArgumentNullException>(() => { _ = 执行器.执行异步(null!); });
    }

    [Fact]
    public async Task 空玩家编号在入队前同步拒绝且不占队列名额()
    {
        var 信号 = 新建信号();
        await using var 执行器 = new 房间执行器(new 房间(Guid.NewGuid(), 8), 1, 信号.Task, 查询队列容量: 1);

        // 用 Action 形式断言：调用本身同步抛出，根本没有返回任务。
        // 命令在构造时就被拒绝，查询在入队前被拒绝。
        Assert.Throws<ArgumentException>(() => { _ = 提交入座(执行器, Guid.Empty, 0); });
        Assert.Throws<ArgumentException>(() => { _ = 提交离座(执行器, Guid.Empty); });
        Assert.Throws<ArgumentException>(() => { _ = 执行器.查找座位异步(Guid.Empty); });
        Assert.Throws<ArgumentException>(() => { _ = 执行器.获取本人视图异步(Guid.Empty); });

        // 两类额度都只有 1，仍然空着，正常请求可以进入，不会收到繁忙。
        var 正常命令 = 提交入座(执行器, 玩家甲, 0);
        var 正常查询 = 执行器.查找座位异步(玩家甲);
        Assert.False(正常命令.IsCompleted);
        Assert.False(正常查询.IsCompleted);
        信号.SetResult(true);
        await 正常命令;
        Assert.Equal<int?>(0, await 正常查询);
    }

    [Fact]
    public async Task 规则拒绝以外的异常视为故障并停止房间()
    {
        var 执行器 = new 房间执行器(Guid.NewGuid(), 8);
        await 提交入座(执行器, 玩家甲, 0);
        await 提交入座(执行器, 玩家乙, 1);
        var 重复牌序 = 牌库.创建标准牌库().ToArray();
        重复牌序[26] = 重复牌序[0];

        // 张数正确，能通过入队前的载荷检查；重复牌是可信流程的缺陷，不是玩家操作被拒。
        await Assert.ThrowsAsync<ArgumentException>(() => 提交发牌(执行器, 重复牌序, new[] { 0, 1 }));

        await 执行器.已停止.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<ArgumentException>(执行器.故障原因);
        await Assert.ThrowsAsync<房间故障异常>(() => 执行器.查找座位异步(玩家甲));
        await 执行器.DisposeAsync();
    }

    [Fact]
    public async Task 可以接管已有房间并继续其状态()
    {
        var 已有房间 = new 房间(Guid.NewGuid(), 8);
        已有房间.入座(玩家甲, 3);

        await using var 执行器 = new 房间执行器(已有房间);

        Assert.Equal(已有房间.房间编号, 执行器.房间编号);
        Assert.Equal<int?>(3, await 执行器.查找座位异步(玩家甲));
        await Assert.ThrowsAsync<规则拒绝异常>(() => 提交入座(执行器, 玩家乙, 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(57)]
    public void 拒绝不支持的命令队列容量(int 容量)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new 房间执行器(Guid.NewGuid(), 8, 容量));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void 拒绝不支持的查询队列容量(int 容量)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new 房间执行器(new 房间(Guid.NewGuid(), 8), 1, Task.CompletedTask, 容量));
    }

    // 每次调用生成新命令编号，相当于玩家各点了一次；测试重发时显式复用同一个命令对象。
    private static Task<命令回执> 提交入座(房间执行器 执行器, Guid 玩家编号, int 座位) =>
        执行器.执行异步(new 入座命令(Guid.NewGuid(), 玩家编号, 座位));

    private static Task<命令回执> 提交离座(房间执行器 执行器, Guid 玩家编号) =>
        执行器.执行异步(new 离座命令(Guid.NewGuid(), 玩家编号));

    private static Task<命令回执> 提交发牌(房间执行器 执行器, IReadOnlyList<牌> 牌序, IReadOnlyList<int> 参与座位顺序) =>
        执行器.执行异步(new 发底牌命令(Guid.NewGuid(), 牌序, 参与座位顺序));

    private static TaskCompletionSource<bool> 新建信号() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
