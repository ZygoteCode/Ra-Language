using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Interpreter.Runtime;
using RaLanguage.Interpreter.Runtime.Async;
using RaLanguage.Interpreter.Values;
using RaLanguage.Interpreter.Values.Async;
using RaLanguage.Interpreter.Values.Primitives;
using RaLanguage.Lexer;

namespace RaLanguage.Interpreter.Values.Functions
{
    public static class AsyncBuiltins
    {
        public static readonly string[] Names =
        {
            "sleep",
            "yield_now",
            "gather",
            "race",
            "timeout",
            "cancel",
            "is_cancelled",
            "is_completed",
            "task_status",
            "current_task",
            "channel",
            "channel_send",
            "channel_recv",
            "channel_close",
            "channel_is_closed",
            "channel_count",
            "to_task",
            "run_blocking",
            "task_result",
            "select"
        };

        public static bool IsAsyncBuiltin(string name)
        {
            for (int i = 0; i < Names.Length; i++)
                if (Names[i] == name) return true;
            return false;
        }

        public static RuntimeResult Execute(string name, List<RuntimeValue> args, Context callerCtx, Position posStart, Position posEnd)
        {
            var res = new RuntimeResult();
            switch (name)
            {
                case "sleep":
                    return BuiltinSleep(args, callerCtx, posStart, posEnd);
                case "yield_now":
                    return BuiltinYieldNow(callerCtx, posStart, posEnd);
                case "gather":
                    return BuiltinGather(args, callerCtx, posStart, posEnd);
                case "race":
                    return BuiltinRace(args, callerCtx, posStart, posEnd);
                case "timeout":
                    return BuiltinTimeout(args, callerCtx, posStart, posEnd);
                case "cancel":
                    return BuiltinCancel(args, callerCtx, posStart, posEnd);
                case "is_cancelled":
                    return BuiltinIsCancelled(args, callerCtx, posStart, posEnd);
                case "is_completed":
                    return BuiltinIsCompleted(args, callerCtx, posStart, posEnd);
                case "task_status":
                    return BuiltinTaskStatus(args, callerCtx, posStart, posEnd);
                case "current_task":
                    return BuiltinCurrentTask(callerCtx, posStart, posEnd);
                case "channel":
                    return BuiltinChannel(args, callerCtx, posStart, posEnd);
                case "channel_send":
                    return BuiltinChannelSend(args, callerCtx, posStart, posEnd);
                case "channel_recv":
                    return BuiltinChannelRecv(args, callerCtx, posStart, posEnd);
                case "channel_close":
                    return BuiltinChannelClose(args, callerCtx, posStart, posEnd);
                case "channel_is_closed":
                    return BuiltinChannelIsClosed(args, callerCtx, posStart, posEnd);
                case "channel_count":
                    return BuiltinChannelCount(args, callerCtx, posStart, posEnd);
                case "to_task":
                    return BuiltinToTask(args, callerCtx, posStart, posEnd);
                case "run_blocking":
                    return BuiltinRunBlocking(args, callerCtx, posStart, posEnd);
                case "task_result":
                    return BuiltinTaskResult(args, callerCtx, posStart, posEnd);
                case "select":
                    return BuiltinSelect(args, callerCtx, posStart, posEnd);
            }
            return res.Failure(new RuntimeError(posStart, posEnd, $"Unknown async builtin '{name}'", callerCtx));
        }

        private static int ExtractInt(RuntimeValue v)
        {
            if (v is IntegerValue iv) return (int)iv.Value;
            if (v is NumberValue nv)
            {
                try { return (int)nv.Value; } catch { }
                try { return (int)Convert.ToInt64(nv.ToString(), System.Globalization.CultureInfo.InvariantCulture); } catch { }
            }
            if (v is LongValue lv) return (int)lv.Value;
            return 0;
        }

        private static RuntimeResult BuiltinSleep(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count != 1) return res.Failure(new RuntimeError(p1, p2, "sleep(ms) requires 1 argument", ctx));
            int ms = ExtractInt(args[0]);
            var parentAsync = ctx?.AsyncCtx;
            var task = AsyncScheduler.Schedule("sleep", parentAsync, childCtx =>
            {
                try
                {
                    if (ms > 0)
                    {
                        var wait = Task.Delay(ms, childCtx.Token);
                        wait.GetAwaiter().GetResult();
                    }
                    return ((RuntimeValue?)NullValue.Null.SetPos(p1, p2), (Error?)null);
                }
                catch (OperationCanceledException)
                {
                    return (null, AsyncScheduler.MakeCancellationError(p1, p2, ctx, "Sleep cancelled"));
                }
            });
            return res.Success(new TaskValue(task).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinYieldNow(Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            var task = AsyncScheduler.Schedule("yield_now", ctx?.AsyncCtx, childCtx =>
            {
                Thread.Yield();
                return ((RuntimeValue?)NullValue.Null.SetPos(p1, p2), (Error?)null);
            });
            return res.Success(new TaskValue(task).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinGather(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            var tasks = new List<RaTaskCore>(args.Count);
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] is TaskValue tv) tasks.Add(tv.Core);
                else if (args[i] is ListValue lv)
                {
                    foreach (var e in lv.Elements)
                    {
                        if (e is TaskValue ttv) tasks.Add(ttv.Core);
                        else return res.Failure(new RuntimeError(p1, p2, $"gather: list element is not a task (got '{e.Type}')", ctx));
                    }
                }
                else return res.Failure(new RuntimeError(p1, p2, $"gather: argument {i} is not a task (got '{args[i].Type}')", ctx));
            }

            var outer = AsyncScheduler.Schedule("gather", ctx?.AsyncCtx, childCtx =>
            {
                var results = new List<RuntimeValue>(tasks.Count);
                for (int i = 0; i < tasks.Count; i++)
                {
                    var t = tasks[i];
                    try { t.Wait(childCtx.Token); }
                    catch (OperationCanceledException)
                    {
                        for (int j = 0; j < tasks.Count; j++) tasks[j].RequestCancel();
                        return (null, AsyncScheduler.MakeCancellationError(p1, p2, ctx, "gather cancelled"));
                    }
                    if (t.IsCancelled)
                    {
                        for (int j = 0; j < tasks.Count; j++) tasks[j].RequestCancel();
                        return (null, AsyncScheduler.MakeCancellationError(p1, p2, ctx, $"gather child {t.DebugName} cancelled"));
                    }
                    if (t.IsFaulted)
                    {
                        for (int j = 0; j < tasks.Count; j++) tasks[j].RequestCancel();
                        return (null, t.Error);
                    }
                    results.Add(t.Result ?? NullValue.Null);
                }
                return ((RuntimeValue?)new ListValue(results).SetPos(p1, p2), (Error?)null);
            });

            return res.Success(new TaskValue(outer).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinRace(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            var tasks = new List<RaTaskCore>(args.Count);
            foreach (var a in args)
            {
                if (a is TaskValue tv) tasks.Add(tv.Core);
                else if (a is ListValue lv)
                {
                    foreach (var e in lv.Elements)
                    {
                        if (e is TaskValue ttv) tasks.Add(ttv.Core);
                        else return res.Failure(new RuntimeError(p1, p2, "race: list element is not a task", ctx));
                    }
                }
                else return res.Failure(new RuntimeError(p1, p2, "race: argument is not a task", ctx));
            }
            if (tasks.Count == 0)
                return res.Failure(new RuntimeError(p1, p2, "race requires at least one task", ctx));

            var outer = AsyncScheduler.Schedule("race", ctx?.AsyncCtx, childCtx =>
            {
                var netTasks = new Task[tasks.Count];
                for (int i = 0; i < tasks.Count; i++) netTasks[i] = tasks[i].AsTask;
                int idx;
                try { idx = Task.WaitAny(netTasks, childCtx.Token); }
                catch (OperationCanceledException)
                {
                    foreach (var t in tasks) t.RequestCancel();
                    return (null, AsyncScheduler.MakeCancellationError(p1, p2, ctx, "race cancelled"));
                }
                var winner = tasks[idx];
                for (int j = 0; j < tasks.Count; j++) if (j != idx) tasks[j].RequestCancel();
                if (winner.IsCancelled) return (null, AsyncScheduler.MakeCancellationError(p1, p2, ctx, $"race winner {winner.DebugName} cancelled"));
                if (winner.IsFaulted) return (null, winner.Error);
                return (winner.Result, null);
            });
            return res.Success(new TaskValue(outer).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinTimeout(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count != 2) return res.Failure(new RuntimeError(p1, p2, "timeout(ms, task) requires 2 arguments", ctx));
            int ms = ExtractInt(args[0]);
            if (args[1] is not TaskValue tv) return res.Failure(new RuntimeError(p1, p2, "timeout: second arg must be a task", ctx));
            var inner = tv.Core;

            var outer = AsyncScheduler.Schedule("timeout", ctx?.AsyncCtx, childCtx =>
            {
                var winner = Task.WaitAny(new Task[] { inner.AsTask, Task.Delay(ms, childCtx.Token) });
                if (winner == 1)
                {
                    inner.RequestCancel();
                    return (null, AsyncScheduler.MakeTimeoutError(p1, p2, ctx, ms));
                }
                if (inner.IsCancelled) return (null, AsyncScheduler.MakeCancellationError(p1, p2, ctx));
                if (inner.IsFaulted) return (null, inner.Error);
                return (inner.Result, null);
            });
            return res.Success(new TaskValue(outer).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinCancel(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count != 1) return res.Failure(new RuntimeError(p1, p2, "cancel(task) requires 1 argument", ctx));
            if (args[0] is TaskValue tv)
            {
                tv.Core.RequestCancel();
                return res.Success(NullValue.Null.SetContext(ctx).SetPos(p1, p2));
            }
            if (args[0] is AsyncStreamValue sv)
            {
                sv.Core.Cancel();
                return res.Success(NullValue.Null.SetContext(ctx).SetPos(p1, p2));
            }
            if (args[0] is ChannelValue cv)
            {
                cv.Channel.Close();
                return res.Success(NullValue.Null.SetContext(ctx).SetPos(p1, p2));
            }
            return res.Failure(new RuntimeError(p1, p2, "cancel: argument must be a task, stream, or channel", ctx));
        }

        private static RuntimeResult BuiltinIsCancelled(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count != 1 || args[0] is not TaskValue tv) return res.Failure(new RuntimeError(p1, p2, "is_cancelled(task)", ctx));
            return res.Success(BooleanValue.Of(tv.Core.IsCancelled).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinIsCompleted(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count != 1 || args[0] is not TaskValue tv) return res.Failure(new RuntimeError(p1, p2, "is_completed(task)", ctx));
            return res.Success(BooleanValue.Of(tv.Core.IsCompleted).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinTaskStatus(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count != 1 || args[0] is not TaskValue tv) return res.Failure(new RuntimeError(p1, p2, "task_status(task)", ctx));
            return res.Success(new StringValue(tv.Core.Status.ToString()).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinCurrentTask(Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            var cur = ctx?.AsyncCtx?.CurrentTask;
            if (cur == null) return res.Success(NullValue.Null.SetContext(ctx).SetPos(p1, p2));
            return res.Success(new TaskValue(cur).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinChannel(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            int cap = args.Count >= 1 ? ExtractInt(args[0]) : 1;
            return res.Success(new ChannelValue(new AsyncChannel(cap <= 0 ? 1 : cap)).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinChannelSend(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count != 2 || args[0] is not ChannelValue cv) return res.Failure(new RuntimeError(p1, p2, "channel_send(ch, value)", ctx));
            var value = args[1];
            if (value != null && value.Type != RuntimeValueType.Null)
            {
                if (cv.ElementType == null)
                {
                    cv.ElementType = RaLanguage.Types.TypeSystem.GetDescriptorFromRuntimeValue(value);
                }
                else if (!cv.ElementType.IsTypeParameter && !RaLanguage.Types.TypeSystem.IsAssignable(ctx!, cv.ElementType, value))
                {
                    return res.Failure(new RuntimeError(p1, p2, $"Channel type mismatch: expected '{cv.ElementType}', got '{value.Type}'", ctx));
                }
            }
            var token = ctx?.AsyncCtx?.Token ?? CancellationToken.None;
            var ok = cv.Channel.Send(value, token);
            return res.Success(BooleanValue.Of(ok).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinChannelRecv(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count != 1 || args[0] is not ChannelValue cv) return res.Failure(new RuntimeError(p1, p2, "channel_recv(ch)", ctx));
            var token = ctx?.AsyncCtx?.Token ?? CancellationToken.None;
            var (ok, value, closed) = cv.Channel.Receive(token);
            if (!ok && closed) return res.Success(NullValue.Null.SetContext(ctx).SetPos(p1, p2));
            if (!ok) return res.Failure(new RuntimeError(p1, p2, "channel_recv cancelled", ctx));
            return res.Success((value ?? NullValue.Null).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinChannelClose(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count != 1 || args[0] is not ChannelValue cv) return res.Failure(new RuntimeError(p1, p2, "channel_close(ch)", ctx));
            cv.Channel.Close();
            return res.Success(NullValue.Null.SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinChannelIsClosed(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count != 1 || args[0] is not ChannelValue cv) return res.Failure(new RuntimeError(p1, p2, "channel_is_closed(ch)", ctx));
            return res.Success(BooleanValue.Of(cv.Channel.IsClosed).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinChannelCount(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count != 1 || args[0] is not ChannelValue cv) return res.Failure(new RuntimeError(p1, p2, "channel_count(ch)", ctx));
            return res.Success(new IntegerValue(cv.Channel.Count).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinToTask(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count != 1) return res.Failure(new RuntimeError(p1, p2, "to_task(value)", ctx));
            return res.Success(new TaskValue(RaTaskCore.FromCompletedValue(args[0])).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinRunBlocking(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count != 1 || args[0] is not TaskValue tv) return res.Failure(new RuntimeError(p1, p2, "run_blocking(task)", ctx));
            tv.Core.Wait();
            if (tv.Core.IsCancelled) return res.Failure(AsyncScheduler.MakeCancellationError(p1, p2, ctx));
            if (tv.Core.IsFaulted && tv.Core.Error != null) return res.Failure(tv.Core.Error);
            return res.Success((tv.Core.Result ?? NullValue.Null).SetContext(ctx).SetPos(p1, p2));
        }

        // select(cases...) -> task<tuple<int, value, bool>>
        //
        // Awaits the first ready case among tasks, channels and async streams.
        // Returns a 3-tuple:
        //   index : 0-based index of the ready case in the input list
        //   value : value produced by the ready case (null if closed/cancelled)
        //   ok    : true on successful read, false when the case ended (closed,
        //           cancelled, faulted — caller can decide how to react)
        //
        // Non-destructive: losing cases keep their state (their tasks keep running,
        // channels/streams keep their unread items). This is the structured-concurrency
        // friendly behaviour. Use race() when you want loser cancellation.
        //
        // Errors / cancellation:
        //   * if the winning Task faulted -> the select task faults with that error.
        //   * if the parent fiber is cancelled -> select cancels.
        //   * channel/stream "closed" is NOT an error: returns ok=false instead.
        //
        // Simultaneous completions: the implementation picks the lowest index among
        // those ready at the wake-up point, giving a deterministic tiebreaker that
        // mirrors hand-written priority order.
        private static RuntimeResult BuiltinSelect(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count == 0)
                return res.Failure(new RuntimeError(p1, p2, "select requires at least one awaitable", ctx));

            // Flatten lists for ergonomics: select(list_of_cases) just works.
            var flat = new List<RuntimeValue>(args.Count);
            foreach (var a in args)
            {
                if (a is ListValue lv)
                {
                    foreach (var e in lv.Elements) flat.Add(e);
                }
                else flat.Add(a);
            }
            for (int i = 0; i < flat.Count; i++)
            {
                if (flat[i] is not (TaskValue or ChannelValue or AsyncStreamValue))
                    return res.Failure(new RuntimeError(p1, p2, $"select: case {i} must be a task, channel or stream (got '{flat[i].Type}')", ctx));
            }

            var cases = flat;
            var outer = AsyncScheduler.Schedule("select", ctx?.AsyncCtx, childCtx =>
            {
                var token = childCtx.Token;
                var waitTasks = new Task[cases.Count];
                for (int i = 0; i < cases.Count; i++)
                {
                    waitTasks[i] = cases[i] switch
                    {
                        TaskValue tv => tv.Core.AsTask,
                        ChannelValue cv => cv.Channel.WhenReadable(token),
                        AsyncStreamValue sv => sv.Core.WhenReadable(token),
                        _ => Task.CompletedTask
                    };
                }

                int idx;
                try { idx = Task.WaitAny(waitTasks, token); }
                catch (OperationCanceledException)
                {
                    return (null, AsyncScheduler.MakeCancellationError(p1, p2, ctx, "select cancelled"));
                }

                // Tiebreaker: prefer the lowest-indexed already-completed case.
                for (int i = 0; i < idx; i++)
                {
                    if (waitTasks[i].IsCompleted) { idx = i; break; }
                }

                var winner = cases[idx];
                RuntimeValue? value = null;
                bool ok = true;

                switch (winner)
                {
                    case TaskValue tv:
                    {
                        if (tv.Core.IsCancelled) { value = NullValue.Null; ok = false; break; }
                        if (tv.Core.IsFaulted) { return (null, tv.Core.Error); }
                        value = tv.Core.Result ?? NullValue.Null;
                        break;
                    }
                    case ChannelValue cv:
                    {
                        var (gotItem, recvValue, closed) = cv.Channel.Receive(token);
                        if (!gotItem && closed) { value = NullValue.Null; ok = false; }
                        else if (!gotItem) { return (null, AsyncScheduler.MakeCancellationError(p1, p2, ctx, "select recv cancelled")); }
                        else { value = recvValue ?? NullValue.Null; }
                        break;
                    }
                    case AsyncStreamValue sv:
                    {
                        var pull = sv.Core.PullNext(token);
                        if (pull.error != null) return (null, pull.error);
                        if (pull.closed) { value = NullValue.Null; ok = false; }
                        else if (!pull.ok) { return (null, AsyncScheduler.MakeCancellationError(p1, p2, ctx, "select pull cancelled")); }
                        else { value = pull.value ?? NullValue.Null; }
                        break;
                    }
                }

                var tuple = new TupleValue(new List<RuntimeValue> {
                    new IntegerValue(idx).SetPos(p1, p2),
                    value!.SetPos(p1, p2),
                    BooleanValue.Of(ok).SetPos(p1, p2)
                }).SetPos(p1, p2);

                return ((RuntimeValue?)tuple, (Error?)null);
            });

            return res.Success(new TaskValue(outer).SetContext(ctx).SetPos(p1, p2));
        }

        private static RuntimeResult BuiltinTaskResult(List<RuntimeValue> args, Context ctx, Position p1, Position p2)
        {
            var res = new RuntimeResult();
            if (args.Count != 1 || args[0] is not TaskValue tv) return res.Failure(new RuntimeError(p1, p2, "task_result(task)", ctx));
            if (!tv.Core.IsCompleted) return res.Failure(new RuntimeError(p1, p2, "task_result: task not yet completed (use await)", ctx));
            if (tv.Core.IsCancelled) return res.Failure(AsyncScheduler.MakeCancellationError(p1, p2, ctx));
            if (tv.Core.IsFaulted && tv.Core.Error != null) return res.Failure(tv.Core.Error);
            return res.Success((tv.Core.Result ?? NullValue.Null).SetContext(ctx).SetPos(p1, p2));
        }
    }
}
