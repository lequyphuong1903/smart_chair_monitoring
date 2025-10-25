namespace PatientMonitoring.Onnx
{
    public static class OutputRuntime
    {
        public static int counthr { get; set; }
        public static int[] historyhr { get; set; }
        public static int outputhr { get; set; }
        public static int countbr { get; set; }
        public static int[] historybr { get; set; }
        public static int outputbr { get; set; }
        static OutputRuntime()
        {
            counthr = 0;
            historyhr = new int[5];
            outputhr = 0;

            countbr = 0;
            historybr = new int[5];
            outputbr = 0;
        }
    }
}
