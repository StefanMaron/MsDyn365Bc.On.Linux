// Stub for System.Diagnostics.PerformanceCounter — no-op on Linux.
// The real implementation calls WindowsIdentity.GetCurrent() which crashes.

namespace System.Diagnostics
{
    public class PerformanceCounter : IDisposable
    {
        public string CategoryName { get; set; } = "";
        public string CounterName { get; set; } = "";
        public string InstanceName { get; set; } = "";
        public string MachineName { get; set; } = ".";
        public bool ReadOnly { get; set; } = true;
        public long RawValue { get; set; }
        public PerformanceCounterInstanceLifetime InstanceLifetime { get; set; }

        public PerformanceCounter() { }
        public PerformanceCounter(string categoryName, string counterName)
        { CategoryName = categoryName; CounterName = counterName; }
        public PerformanceCounter(string categoryName, string counterName, string instanceName)
        { CategoryName = categoryName; CounterName = counterName; InstanceName = instanceName; }
        public PerformanceCounter(string categoryName, string counterName, string instanceName, bool readOnly)
        { CategoryName = categoryName; CounterName = counterName; InstanceName = instanceName; ReadOnly = readOnly; }

        public long Increment() => ++RawValue;
        public long Decrement() => --RawValue;
        public long IncrementBy(long value) { RawValue += value; return RawValue; }
        public float NextValue() => 0f;
        public void RemoveInstance() { }
        public void Close() { }
        public void Dispose() { }
    }

    public class PerformanceCounterCategory
    {
        public string CategoryName { get; set; } = "";

        public PerformanceCounterCategory() { }
        public PerformanceCounterCategory(string categoryName) { CategoryName = categoryName; }

        public static bool Exists(string categoryName) => false;
        public static bool Exists(string categoryName, string machineName) => false;
        public bool CounterExists(string counterName) => false;
        public bool InstanceExists(string instanceName) => false;
        public static void Delete(string categoryName) { }
        public static PerformanceCounterCategory Create(string categoryName, string categoryHelp,
            PerformanceCounterCategoryType categoryType, CounterCreationDataCollection counterData)
            => new PerformanceCounterCategory(categoryName);
    }

    public enum PerformanceCounterCategoryType { Unknown = -1, SingleInstance = 0, MultiInstance = 1 }
    public enum PerformanceCounterInstanceLifetime { Global = 0, Process = 1 }
    public enum PerformanceCounterType
    {
        NumberOfItems32 = 65536,
        NumberOfItems64 = 65792,
        RateOfCountsPerSecond32 = 272696320,
        RateOfCountsPerSecond64 = 272696576,
        AverageTimer32 = 805438464,
        AverageBase = 1073939458,
        AverageCount64 = 1073874176,
        RawFraction = 537003008,
        RawBase = 1073939459,
    }

    public class CounterCreationDataCollection : System.Collections.CollectionBase
    {
        public int Add(CounterCreationData value) { InnerList.Add(value); return InnerList.Count - 1; }
    }

    public class CounterCreationData
    {
        public string CounterName { get; set; } = "";
        public string CounterHelp { get; set; } = "";
        public PerformanceCounterType CounterType { get; set; }

        public CounterCreationData() { }
        public CounterCreationData(string counterName, string counterHelp, PerformanceCounterType counterType)
        { CounterName = counterName; CounterHelp = counterHelp; CounterType = counterType; }
    }
}
