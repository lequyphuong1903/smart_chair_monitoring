using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PatientMonitoring.Models
{
    public static class Config
    {
        public static string Host { get; set; }
        public static int Port { get; set; }
        public static int MaxPoints { get; set; }
        static Config()
        {
            Host = "127.0.0.1";
            Port = 12345;
            MaxPoints = 1000;
        }
    }
}
