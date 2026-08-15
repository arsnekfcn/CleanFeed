using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Sandbox.Game.Gui;
using VRage.Render11.Sprites;
using VRageRender;
using VRageRender.Messages;

namespace CleanFeed.Render
{
    internal enum VanillaHudSealDecision
    {
        Fallback,
        Publish,
        Clear,
        RetainNoGeneration,
        RetainMismatch
    }

    // MyGuiScreenHudSpace records one complete asynchronous HUD draw into a deferred command object.
    // Render11 recursively processes that object with one FrameId, which is also attached to every
    // named sprite bucket it touches. Tagging the command object lets the selective consumer publish
    // only complete vanilla generations instead of guessing completeness from aggregate sprite count.
    internal static class VanillaHudGenerationSeal
    {
        internal sealed class ProcessedCommand
        {
            internal long Sequence;
            internal int FrameId;
        }

        private sealed class CommandTag
        {
            internal long Sequence;
        }

        private static readonly object TagGate = new object();
        private static ConditionalWeakTable<object, CommandTag> _tags =
            new ConditionalWeakTable<object, CommandTag>();
        private static long _nextSequence;
        private static long _latestProcessedSequence;
        private static long _handledSequence;
        private static int _latestProcessedFrameId = -1;
        private static long _tagged;
        private static long _processed;
        private static long _published;
        private static long _explicitClears;
        private static long _noGenerationRetains;
        private static long _mismatchRetains;
        private static long _fallbacks;
        private static int _lastBucketFrameId = -1;

        internal static void Tag(object command)
        {
            if (command == null) return;
            var tag = new CommandTag { Sequence = Interlocked.Increment(ref _nextSequence) };
            lock (TagGate)
            {
                _tags.Remove(command);
                _tags.Add(command, tag);
            }
            Interlocked.Increment(ref _tagged);
        }

        internal static ProcessedCommand BeginProcess(object command, int frameId)
        {
            // ProcessMessage is a very hot render seam. Reject ordinary messages before touching
            // the weak-tag table or taking its lock.
            if (!(command is MyRenderMessageDrawCommands)) return null;
            CommandTag tag;
            lock (TagGate)
            {
                if (!_tags.TryGetValue(command, out tag)) return null;
                // Draw-command messages are pooled. Consume the weak tag now so a later unrelated
                // use of the same pooled object can never inherit this HUD generation identity.
                _tags.Remove(command);
            }
            return new ProcessedCommand { Sequence = tag.Sequence, FrameId = frameId };
        }

        internal static void CompleteProcess(ProcessedCommand command)
        {
            if (command == null) return;
            Volatile.Write(ref _latestProcessedFrameId, command.FrameId);
            Interlocked.Exchange(ref _latestProcessedSequence, command.Sequence);
            Interlocked.Increment(ref _processed);
        }

        internal static VanillaHudSealDecision Classify(MySpriteMessageData messages)
        {
            int bucketFrameId = messages?.FrameId ?? -1;
            Volatile.Write(ref _lastBucketFrameId, bucketFrameId);
            long latest = Interlocked.Read(ref _latestProcessedSequence);
            if (latest == 0)
            {
                Interlocked.Increment(ref _fallbacks);
                return VanillaHudSealDecision.Fallback;
            }

            long handled = Interlocked.Read(ref _handledSequence);
            if (latest <= handled)
            {
                Interlocked.Increment(ref _noGenerationRetains);
                return VanillaHudSealDecision.RetainNoGeneration;
            }

            Interlocked.Exchange(ref _handledSequence, latest);
            int sealedFrameId = Volatile.Read(ref _latestProcessedFrameId);
            if (messages == null)
            {
                Interlocked.Increment(ref _explicitClears);
                return VanillaHudSealDecision.Clear;
            }
            if (bucketFrameId == sealedFrameId)
            {
                Interlocked.Increment(ref _published);
                return VanillaHudSealDecision.Publish;
            }

            Interlocked.Increment(ref _mismatchRetains);
            return VanillaHudSealDecision.RetainMismatch;
        }

        internal static string StatusLine()
            => "vanilla-seal=tagged:" + Interlocked.Read(ref _tagged)
               + ", processed:" + Interlocked.Read(ref _processed)
               + ", published:" + Interlocked.Read(ref _published)
               + ", clear:" + Interlocked.Read(ref _explicitClears)
               + ", keep-no-generation:" + Interlocked.Read(ref _noGenerationRetains)
               + ", keep-mismatch:" + Interlocked.Read(ref _mismatchRetains)
               + ", fallback:" + Interlocked.Read(ref _fallbacks)
               + ", latest=" + Interlocked.Read(ref _latestProcessedSequence)
               + "@" + Volatile.Read(ref _latestProcessedFrameId)
               + ", handled=" + Interlocked.Read(ref _handledSequence)
               + ", bucket=" + Volatile.Read(ref _lastBucketFrameId);

        internal static void Reset()
        {
            lock (TagGate) _tags = new ConditionalWeakTable<object, CommandTag>();
            Interlocked.Exchange(ref _latestProcessedSequence, 0);
            Interlocked.Exchange(ref _handledSequence, 0);
            Volatile.Write(ref _latestProcessedFrameId, -1);
            Volatile.Write(ref _lastBucketFrameId, -1);
        }
    }

    [HarmonyPatch]
    internal static class VanillaHudDrawCompletionPatch
    {
        private static readonly FieldInfo DrawMessagesField =
            AccessTools.Field(typeof(MyGuiScreenHudSpace), "m_drawAsyncMessages");

        private static MethodBase TargetMethod()
            => AccessTools.Method(typeof(MyGuiScreenHudSpace), "DrawAsync", Type.EmptyTypes);

        private static bool Prepare() => TargetMethod() != null && DrawMessagesField != null;

        private static void Prefix() => HudRedirector.ObserveFocusPrivacy();

        private static void Postfix(object __instance)
            => VanillaHudGenerationSeal.Tag(DrawMessagesField.GetValue(__instance));
    }

    [HarmonyPatch]
    internal static class VanillaHudRenderCompletionPatch
    {
        private static MethodBase TargetMethod()
            => AccessTools.Method(typeof(MyRender11), "ProcessMessage",
                new[] { typeof(MyRenderMessageBase), typeof(int) });

        private static bool Prepare() => TargetMethod() != null;

        private static void Prefix(
            MyRenderMessageBase message,
            int frameId,
            out VanillaHudGenerationSeal.ProcessedCommand __state)
            => __state = VanillaHudGenerationSeal.BeginProcess(message, frameId);

        private static void Postfix(VanillaHudGenerationSeal.ProcessedCommand __state)
            => VanillaHudGenerationSeal.CompleteProcess(__state);
    }
}
