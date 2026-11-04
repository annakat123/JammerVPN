using System.Diagnostics;
using System.Reflection;
using Jammer.Core;

static class EventWaitCheck
{
    public static void Run()
    {
        using var owner = new EventWaitHandle(false, EventResetMode.ManualReset);
        var wrap = typeof(WinTun).GetMethod("BorrowReadEvent", BindingFlags.Static | BindingFlags.NonPublic)!;
        EventWaitHandle Borrow() => (EventWaitHandle)wrap.Invoke(null, new object[] { owner.SafeWaitHandle.DangerousGetHandle() })!;
        using (var packet = Borrow())
        using (var cancel = new CancellationTokenSource())
        {
            var reader = Task.Run(() => WaitHandle.WaitAny(new[] { cancel.Token.WaitHandle, packet }));
            if (reader.Wait(30)) throw new Exception("Wait returned without a packet or cancellation");
            owner.Set();
            if (!reader.Wait(1000) || reader.Result != 1) throw new Exception("Packet event did not wake reader");
            owner.Reset();
            var stopped = Task.Run(() => WaitHandle.WaitAny(new[] { cancel.Token.WaitHandle, packet }));
            cancel.Cancel();
            if (!stopped.Wait(1000) || stopped.Result != 0) throw new Exception("Cancellation did not stop idle reader");
        }
        owner.Set();
        if (!owner.WaitOne(0)) throw new Exception("Borrowed wrapper closed the Wintun-owned event");
        using (var again = Borrow()) if (!again.WaitOne(0)) throw new Exception("Existing signal was missed");
        Console.WriteLine("PASS: packet arrival wakes an idle reader without timer polling");
        Console.WriteLine("PASS: cancellation unblocks idle packet waiting");
        Console.WriteLine("PASS: managed wrapper leaves native event ownership intact and preserves existing signals");
    }
}
