using MP4Reader;
using System;
using System.Collections.Generic;

namespace Cromatix.MP4Reader
{
    public class Telemetry
    {
        public string FileName { get; set; }
        public string DeviceName { get; set; }
        public string Description { get; set; }
        public List<LocationKLV> LocationKLVs { get; set; }
        public List<AttitudeKLV> AttitudeKLVs { get; set; }

        public DateTime? StartTime { get; set; }
    }
}
