
using Microsoft.ML.OnnxRuntime;
using PatientMonitoring.Onnx;
using PatientMonitoring.Services;

namespace PatientMonitoring.Models
{
    public static class HRRR
    {
        private static int HRValue { get; set; }
        private static int currentHRValue { get; set; }
        private static int previousHRValue1 { get; set; }
        private static int previousHRValue2 { get; set; }
        private static int BRValue { get; set; }
        private static int currentBRValue { get; set; }
        private static int previousBRValue1 { get; set; }
        private static int previousBRValue2 { get; set; }
        static HRRR()
        {

        }
        public static int UpdateHR()
        {
            int[] arr = RemoveOutliersUsingIQR(OutputRuntime.historyhr);
            int HRtemp = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                HRtemp += arr[i];
            }
            HRtemp /= arr.Length;
            HRValue = HRtemp;
            if (HRValue < (previousHRValue1 - 4) || HRValue > (previousHRValue1 + 4))
            {
                if (HRValue < (previousHRValue2 - 4) || HRValue > (previousHRValue2 + 4))
                {
                    currentHRValue = HRValue;
                }
            }
            else
            {
                currentHRValue = HRValue;
            }
            previousHRValue2 = previousHRValue1;
            previousHRValue1 = HRValue;
            return currentHRValue;
        }
        public static int UpdateBR()
        {
            int[] arr = RemoveOutliersUsingIQR(FindPeaks.history);
            int BRtemp = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                BRtemp += arr[i];
            }
            BRtemp /= arr.Length + 1;
            BRValue = BRtemp;
            if (BRValue < (previousBRValue1 - 2) || BRValue > (previousBRValue1 + 2))
            {
                if (BRValue < (previousBRValue2 - 2) || BRValue > (previousBRValue2 + 2))
                {
                    currentBRValue = BRValue;
                }
            }
            else
            {
                currentBRValue = BRValue;
            }
            previousBRValue2 = previousBRValue1;
            previousBRValue1 = BRValue;
            return currentBRValue;
        }
        static int[] RemoveOutliersUsingIQR(int[] arr)
        {
            Array.Sort(arr);

            double q1 = GetPercentile(arr, 25);
            double q3 = GetPercentile(arr, 75);
            double iqr = q3 - q1;

            double lowerBound = q1 - 1.5 * iqr;
            double upperBound = q3 + 1.5 * iqr;

            return arr.Where(val => val >= lowerBound && val <= upperBound).ToArray();
        }
        static double GetPercentile(int[] sortedArray, double percentile)
        {
            double realIndex = percentile / 100.0 * (sortedArray.Length - 1);
            int index = (int)realIndex;
            double frac = realIndex - index;

            if (index + 1 < sortedArray.Length)
                return sortedArray[index] * (1 - frac) + sortedArray[index + 1] * frac;
            else
                return sortedArray[index];
        }
    }
}
