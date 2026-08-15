using System.Threading;

namespace CleanFeed.Render
{
    // Allocation-free ownership counters. Expected steady-state live counts while redirect is active
    // are 14 native DirectComposition references - one device, one target, and a visual plus surface
    // for each of the six composition layers - and zero of everything else between frames.
    internal static class HudResourceTelemetry
    {
        private static long _wholeBorrows;
        private static long _wholeReleases;
        private static long _selectiveBorrows;
        private static long _selectiveReleases;
        private static long _nativeAcquires;
        private static long _nativeReleases;
        private static long _beginDraws;
        private static long _endDraws;
        private static long _endDrawFailures;
        private static long _updateObjectAcquires;
        private static long _updateObjectReleases;
        private static long _copyDestinationAcquires;
        private static long _copyDestinationReleases;
        private static long _maxBorrowedLive;
        private static long _maxNativeLive;
        private static long _maxUpdateObjectsLive;
        private static long _maxCopyDestinationsLive;

        internal static void WholeBorrowed()
        {
            long value = Interlocked.Increment(ref _wholeBorrows) - Interlocked.Read(ref _wholeReleases);
            RaiseHighWater(ref _maxBorrowedLive, value + SelectiveLive);
        }

        internal static void WholeReleased() => Interlocked.Increment(ref _wholeReleases);

        internal static void SelectiveBorrowed()
        {
            long value = Interlocked.Increment(ref _selectiveBorrows) - Interlocked.Read(ref _selectiveReleases);
            RaiseHighWater(ref _maxBorrowedLive, value + WholeLive);
        }

        internal static void SelectiveReleased() => Interlocked.Increment(ref _selectiveReleases);

        internal static void NativeAcquired()
        {
            long live = Interlocked.Increment(ref _nativeAcquires) - Interlocked.Read(ref _nativeReleases);
            RaiseHighWater(ref _maxNativeLive, live);
        }

        internal static void NativeReleased() => Interlocked.Increment(ref _nativeReleases);

        internal static void BeginDraw()
        {
            Interlocked.Increment(ref _beginDraws);
            long live = Interlocked.Increment(ref _updateObjectAcquires)
                        - Interlocked.Read(ref _updateObjectReleases);
            RaiseHighWater(ref _maxUpdateObjectsLive, live);
        }

        internal static void EndDraw(bool succeeded)
        {
            Interlocked.Increment(ref _endDraws);
            if (!succeeded) Interlocked.Increment(ref _endDrawFailures);
        }

        internal static void UpdateObjectReleased() => Interlocked.Increment(ref _updateObjectReleases);

        internal static void CopyDestinationAcquired()
        {
            long live = Interlocked.Increment(ref _copyDestinationAcquires)
                        - Interlocked.Read(ref _copyDestinationReleases);
            RaiseHighWater(ref _maxCopyDestinationsLive, live);
        }

        internal static void CopyDestinationReleased()
            => Interlocked.Increment(ref _copyDestinationReleases);

        internal static string StatusLine()
            => "resource-live=" + BorrowedLive + "/" + NativeLive + "/" + UpdateObjectLive
               + "/" + CopyDestinationLive + "(rtv/native/update/copy-qi)"
               + ", resource-max=" + Interlocked.Read(ref _maxBorrowedLive)
               + "/" + Interlocked.Read(ref _maxNativeLive)
               + "/" + Interlocked.Read(ref _maxUpdateObjectsLive)
               + "/" + Interlocked.Read(ref _maxCopyDestinationsLive)
               + ", whole=" + Interlocked.Read(ref _wholeBorrows)
               + "/" + Interlocked.Read(ref _wholeReleases)
               + ", selective=" + Interlocked.Read(ref _selectiveBorrows)
               + "/" + Interlocked.Read(ref _selectiveReleases)
               + ", native=" + Interlocked.Read(ref _nativeAcquires)
               + "/" + Interlocked.Read(ref _nativeReleases)
               + ", draw=" + Interlocked.Read(ref _beginDraws)
               + "/" + Interlocked.Read(ref _endDraws)
               + ", update-ref=" + Interlocked.Read(ref _updateObjectAcquires)
               + "/" + Interlocked.Read(ref _updateObjectReleases)
               + ", copy-qi=" + Interlocked.Read(ref _copyDestinationAcquires)
               + "/" + Interlocked.Read(ref _copyDestinationReleases)
               + ", enddraw-failures=" + Interlocked.Read(ref _endDrawFailures);

        private static long WholeLive
            => Interlocked.Read(ref _wholeBorrows) - Interlocked.Read(ref _wholeReleases);

        private static long SelectiveLive
            => Interlocked.Read(ref _selectiveBorrows) - Interlocked.Read(ref _selectiveReleases);

        private static long BorrowedLive => WholeLive + SelectiveLive;

        private static long NativeLive
            => Interlocked.Read(ref _nativeAcquires) - Interlocked.Read(ref _nativeReleases);

        private static long UpdateObjectLive
            => Interlocked.Read(ref _updateObjectAcquires) - Interlocked.Read(ref _updateObjectReleases);

        private static long CopyDestinationLive
            => Interlocked.Read(ref _copyDestinationAcquires)
               - Interlocked.Read(ref _copyDestinationReleases);

        private static void RaiseHighWater(ref long highWater, long value)
        {
            long current;
            while (value > (current = Interlocked.Read(ref highWater))
                   && Interlocked.CompareExchange(ref highWater, value, current) != current) { }
        }
    }
}
