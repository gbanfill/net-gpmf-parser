using System;
using System.Collections.Generic;
using System.Text;

namespace MP4Reader
{
    public class AttitudeKLV
    {
        /// <summary>
        /// The calculated offset from the Base UTC time
        /// </summary>
        public TimeSpan OffsetFromUTCBaseTime { get; set; }

        /// <summary>
        ///  The absolute time (can be calculated from the telemetry.StartTime)
        /// </summary>
        public DateTime? Time { get; set; }

        /// <summary>
        /// Yaw rotation in degrees
        /// </summary>
        public double? Yaw { get; set; }
        /// <summary>
        /// pitch rotation in degrees
        /// </summary>
        public double? Pitch { get; set; }
        /// <summary>
        /// Roll rotation in degrees
        /// </summary>
        public double? Roll { get; set; }
    }
}
