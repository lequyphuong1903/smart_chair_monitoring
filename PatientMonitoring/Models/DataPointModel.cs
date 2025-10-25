namespace PatientMonitoring.Models
{
    public class DataPointModel
    {
        public DateTime Timestamp { get; set; }
        public short HeartValue { get; set; }
        public short BreathValue { get; set; }
        public uint RedValue { get; set; }
        public uint IrValue { get; set; }
        public ushort T1 { get; set; }
        public ushort T2 { get; set; }
        public DataPointModel(DateTime timestamp, short heartValue, short breathValue, uint redValue, uint irValue, ushort t1, ushort t2)
        {
            Timestamp = timestamp;
            HeartValue = heartValue;
            BreathValue = breathValue;
            RedValue = redValue;
            IrValue = irValue;
            T1 = t1;
            T2 = t2;
        }
    }
}
