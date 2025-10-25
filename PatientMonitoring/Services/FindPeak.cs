namespace PatientMonitoring.Services
{
    public static class FindPeaks
    {
        public static int[] history { get; }
        static int index = 0;
        public static int minDistance { get; set; }
        static FindPeaks()
        {
            history = new int[5];
            minDistance = 100;
        }
        public static void FindPeak(float[] data)
        {
            double avg = 0;
            for (int i = 0; i < data.Length; i++)
            {
                avg += data[i];
            }
            avg /= data.Length;
            List<int> rPeaks = new List<int>();
            int lastPeak = -minDistance;

            for (int i = 1; i < data.Length - 1; i++)
            {
                if (data[i] > avg && data[i] > data[i - 1] && data[i] > data[i + 1])
                {
                    if (i - lastPeak >= minDistance)
                    {
                        rPeaks.Add(i);
                        lastPeak = i;
                    }
                }
            }
            if (rPeaks.Count > 2)
            {
                history[index] = (int)(60 / ((rPeaks[rPeaks.Count - 1] - rPeaks[1]) / (rPeaks.Count - 2) * 0.0256));
                index++;
                if (index == 5)
                { index = 0; }
            }
        }
    }
}

