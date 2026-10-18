using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace WallBili.Desktop.Tests;

/// <summary>字体栈回归：界面文字必须真的落在设计稿要求的字体上。
/// 背景：WPF 把"未安装字体"当 pack:// 资源引用时会**静默**回退成默认字体且不报错，
/// 所以这里断言的是解析出来的字族名，而不是"没有抛异常"。</summary>
public sealed class DesignFontTests
{
    [Fact]
    public Task InterIsShippedAndResolvesForBothWeights() => OnSta(() =>
    {
        foreach (var file in new[] { DesignFont.InterRegularFile, DesignFont.InterBoldFile })
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, file)), $"{file} 没有随输出分发");

        var design = new DesignFont();
        var regular = new Typeface(design, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Assert.True(regular.TryGetGlyphTypeface(out var regularFace), "常规字重解析不到字体");
        Assert.Equal("Inter", regularFace.FamilyNames.Values.First());
        Assert.Equal(StyleSimulations.None, regularFace.StyleSimulations);

        var bold = new Typeface(design, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        Assert.True(bold.TryGetGlyphTypeface(out var boldFace), "粗体解析不到字体");
        Assert.Equal(FontWeights.Bold, boldFace.Weight);
        Assert.Equal(StyleSimulations.None, boldFace.StyleSimulations); // 真粗体，不是算法加粗
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheStackFindsACjkFamilyOnThisMachine() => OnSta(() =>
    {
        // 中文按设计稿顺序回退（思源黑体 → Noto Sans SC → 雅黑）。这里验证该顺序在本机能命中一套，
        // 而不是假定每个用户都装了思源黑体。
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hit = DesignFont.ChineseFamilies.FirstOrDefault(installed.Contains);
        Assert.False(string.IsNullOrEmpty(hit), "字体栈里的中文字族一个都没装，中文会渲染成方块");

        var typeface = new Typeface(new FontFamily(hit), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Assert.True(typeface.TryGetGlyphTypeface(out var face));
        Assert.True(face.CharacterToGlyphMap.ContainsKey('桌'), $"{hit} 缺少常用汉字字形");
        return Task.CompletedTask;
    });

    private static async Task OnSta(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
}
