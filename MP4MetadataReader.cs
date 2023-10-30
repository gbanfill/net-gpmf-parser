using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MP4Reader;
using MP4Reader.IO;

namespace Cromatix.MP4Reader
{
    public class MP4MetadataReader
    {
        private const int MAX_TRACKS = 16;
        private const string DEVICE_DATA = "DEVC";
        private const string DEVICE_ID = "DVID";
        private const string DEVICE_NAME = "DVNM";

        private readonly Stream _stream = null;
        private readonly DateTime epoch = new DateTime(1904, 1, 1);
        private readonly DateTime epoch2 = new DateTime(2000, 1, 1);

        private string[] allowedAtoms = new string[] { "moov", "mvhd", "trak", "mdia", "mdhd", "minf", "stsd", "stbl", "stts", "stsc", "stsz", "stco", "hdlr", "edts" };

        private string trakType;
        private string trakSubType;
        private Telemetry telemetry = new Telemetry();

        internal int TrakNum { get; private set; }
        internal int TrakClockDaemon { get; private set; } // time scale
        internal int TrakClockCount { get; private set; } // duration
        internal int ClockDaemon { get; private set; }
        internal int ClockCount { get; private set; }
        internal int MetaClockCount { get; private set; }
        internal int MetaClockDaemon { get; private set; }
        internal int MetaSTSCCount { get; private set; }
        internal double MetadataLength { get; private set; }
        internal double BaseMetadataDuration { get; private set; }
        internal int MetadataOffsetClockCount { get; private set; }
        internal int MetasizeCount { get; private set; }
        internal int SamplesCount { get; private set; }
        internal int[] MetaSizes { get; private set; }
        internal int[] MetaOffsets { get; private set; }
        internal int MetaSTCOCount { get; private set; }

        /// <summary>
        /// Video length in Seconds
        /// </summary>
        internal double VideoLength { get; private set; }
        internal int[] TrakEditOffsets = new int[MAX_TRACKS];
        internal List<SampleToChunk> MetaSTSC { get; private set; }
        internal DateTime CreationTime { get; private set; }
        internal DateTime ModificationTime { get; private set; }
        internal string FileName
        {
            get
            {
                if (_stream != null)
                {
                    return Path.GetFileName((_stream as FileStream).Name);
                }

                return string.Empty;
            }
        }

        public Telemetry Telemetry
        {
            get { return telemetry; }
        }

        public MP4MetadataReader(Stream stream)
        {
            _stream = stream;
            ReadMetadata();
        }

        /// <summary>
        /// Process the track and load all the KLV values from it.
        /// </summary>
        public void ProcessGPMFTelemetry()
        {
            int payloads = GetNumberOfPayloads;
            int gpsIndex = 0;
            bool hasGPS9Data = false;
            telemetry.LocationKLVs = new List<LocationKLV>();
            telemetry.AttitudeKLVs = new List<AttitudeKLV>();

            List<string> seenFourCC = new List<string>();
            for (int index = 0; index < payloads; index++)
            {
                double _in; double _out;
                byte[] payload = GetPayload(index);

                using (GPMFStream gpmf = new GPMFStream(payload))
                {
                    bool isNext;
                    int[] devisors = null;
                    string GPSFix = "0";
                    short DilutionOfPrecision = 0;
                    DateTime? utcStartTime = default(DateTime);

                    Rotations lastRotationData = null;

                    do
                    {
                        isNext = GetGPMF(gpmf);

                        if (!seenFourCC.Contains(gpmf.FourCC))
                        {
                            seenFourCC.Add(gpmf.FourCC);
                        }
                        // GPS Precision - Dilution of Precision (DOP x100)
                        // https://en.wikipedia.org/wiki/Dilution_of_precision_(navigation)
                        if (gpmf.FourCC == "GPSP")
                        {
                            DilutionOfPrecision = ByteUtil.GetShort(gpmf);
                        }

                        // GPS Fix = 0 - no lock, 2 or 3 - 2D or 3D Lock
                        if (gpmf.FourCC == "GPSF")
                        {
                            int fix = ByteUtil.GetInt(gpmf);

                            if (fix == 2)
                            {
                                GPSFix = "2d";
                            }
                            else if (fix == 3)
                            {
                                GPSFix = "3d";
                            }
                        }

                        if (gpmf.FourCC == "SCAL")
                        {
                            int repeats = gpmf.Repeat;
                            int pos = gpmf.Position;
                            devisors = new int[repeats];

                            while (repeats > 0 && gpmf.StructSize == 4)
                            {
                                repeats--;

                                var dataHex = new ReadOnlySpan<byte>(gpmf.Content, pos + 8, gpmf.StructSize).ToArray();
                                var num = ByteUtil.HexToInt((int)ByteUtil.BytesToInt(dataHex, 0));
                                devisors[repeats] = num;
                                pos += 4;
                            }
                        }

                        // UTC date and time from GPS
                        if (gpmf.FourCC == "GPSU" && gpmf.StructSize == 16)
                        {
                            var bytes = new ReadOnlySpan<byte>(gpmf.Content, gpmf.Position + 8, gpmf.StructSize).ToArray();
                            utcStartTime = GPSUToUTCDate(ByteUtil.BytesToString(bytes, false));
                            if (telemetry.StartTime == null)
                            {
                                telemetry.StartTime = utcStartTime.Value;
                            }
                        }

                        if (gpmf.FourCC == "MAGN")
                        {
                            GetPayloadTime(gpsIndex, out _in, out _out);
                            int repeats = gpmf.Repeat;
                            double increment = (_out - _in) / repeats;

                            int pos = gpmf.Position;
                            for (int i = 0; i < repeats; i++)
                            {
                                var klv = new AttitudeKLV();
                                klv.OffsetFromUTCBaseTime = TimeSpan.FromSeconds(_in + (increment * i));

                                var bytes = new ReadOnlySpan<byte>(gpmf.Content, gpmf.Position + 8, gpmf.StructSize).ToArray();
                                var rawData = GetMagnValues(gpmf, devisors, ref pos);
                                lastRotationData = CalculateRotationData(rawData);
                                klv.Yaw = lastRotationData.Yaw;
                                klv.Pitch = lastRotationData.Pitch;
                                klv.Roll = lastRotationData.Roll;
                                telemetry.AttitudeKLVs.Add(klv);
                            }
                        }

                        if (gpmf.FourCC == "GPS9" && gpmf.Repeat > 0 && devisors != null)
                        {
                            int pos = gpmf.Position;
                            int repeats = gpmf.Repeat;

                            devisors = devisors.Reverse().ToArray();

                            if (hasGPS9Data == false && telemetry.LocationKLVs.Count > 0)
                            {
                                hasGPS9Data = true;
                                telemetry.LocationKLVs.Clear();
                            }

                            for (int i = 0; i < repeats; i++)
                            {
                                var klv = new LocationKLV();
                                var cv = GetCoordValues9(gpmf, devisors, ref pos);

                                klv.Time = epoch2.AddDays(cv.d2000).AddSeconds(cv.Seconds);

                                klv.Lat = cv.Lat;
                                klv.Lon = cv.Lon;
                                klv.Alt = cv.Alt;
                                klv.GroundSpeed = cv.Speed2d;
                                klv.VirtualSpeed = cv.Speed3d;
                                klv.HDOP = (int)cv.DOP;

                                switch (cv.Fix)
                                {
                                    case 0:
                                        klv.GPSFix = "0";
                                        break;
                                    case 2:
                                        klv.GPSFix = "2d";
                                        break;
                                    case 3:
                                        klv.GPSFix = "3d";
                                        break;
                                }

                                telemetry.LocationKLVs.Add(klv);
                            }
                        }

                        if (gpmf.FourCC == "GPS5" &&
                            gpmf.Repeat > 0 &&
                            devisors != null &&
                            hasGPS9Data == false)
                        {
                            GetPayloadTime(gpsIndex, out _in, out _out);
                            gpsIndex++;

                            int repeats = gpmf.Repeat;
                            double increment = (_out - _in) / repeats;

                            if (DilutionOfPrecision > 999)
                            {
                                utcStartTime = utcStartTime.Value.AddSeconds(increment);
                                continue;
                            }

                            int pos = gpmf.Position;
                            devisors = devisors.Reverse().ToArray();

                            for (int i = 0; i < repeats; i++)
                            {
                                var klv = new LocationKLV();
                                var cv = GetCoordValues5(gpmf, devisors, ref pos);

                                if (telemetry.LocationKLVs.Count > 0 && telemetry.LocationKLVs[telemetry.LocationKLVs.Count - 1].Time != null)
                                {
                                    klv.Time = utcStartTime.Value.AddSeconds(increment);
                                    utcStartTime = klv.Time.Value;
                                }
                                else
                                {
                                    klv.Time = utcStartTime;
                                }

                                klv.Lat = cv.Lat;
                                klv.Lon = cv.Lon;
                                klv.Alt = cv.Alt;
                                klv.GroundSpeed = cv.Speed2d;
                                klv.VirtualSpeed = cv.Speed3d;
                                klv.HDOP = DilutionOfPrecision;
                                klv.GPSFix = GPSFix;

                                telemetry.LocationKLVs.Add(klv);
                            }
                        }
                    }
                    while (isNext);
                }
            }

            foreach (var attitudeKlv in telemetry.AttitudeKLVs)
            {
                attitudeKlv.Time = telemetry.StartTime + attitudeKlv.OffsetFromUTCBaseTime;
            }

            Debug.WriteLine($"Video Duration: {TimeSpan.FromSeconds(VideoLength)}");
            Debug.WriteLine($"GPS:  Duration {telemetry.LocationKLVs.Last().Time - telemetry.LocationKLVs.First().Time}. {telemetry.LocationKLVs.Count} data points. From {telemetry.LocationKLVs.First().Time} to {telemetry.LocationKLVs.Last().Time}");
            Debug.WriteLine($"MAGN: Duration {telemetry.AttitudeKLVs.Last().Time - telemetry.AttitudeKLVs.First().Time}. {telemetry.AttitudeKLVs.Count} data points. From {telemetry.AttitudeKLVs.First().Time} to {telemetry.AttitudeKLVs.Last().Time}");
        }

        double AverageX = 156.23;
        double AverageY = 21.3;
        private Rotations CalculateRotationData(Magn rawData)
        {
            var normalizedX = rawData.X - AverageX;
            var normalizedY = (rawData.Y - AverageY) * -1;
            var yaw = (Math.Atan2(normalizedY, normalizedX) * (180 / Math.PI) + 360) % 360;
            return new Rotations
            {
                RawData = rawData,
                Yaw = yaw,
            };
        }

        private Magn GetMagnValues(GPMFStream gpmf, int[] elements, ref int pos)
        {
            var content = new ReadOnlySpan<byte>(gpmf.Content, pos + 8, gpmf.StructSize);
            var result = new Magn
            {
                X = ByteUtil.ReadShort(ref content),
                Y = ByteUtil.ReadShort(ref content),
                Z = ByteUtil.ReadShort(ref content),
            };
            pos += gpmf.StructSize;
            return result;
        }

        private GPS5 GetCoordValues5(GPMFStream gpmf, int[] elements, ref int pos)
        {
            var content = new ReadOnlySpan<byte>(gpmf.Content, pos + 8, gpmf.StructSize);
            var result = new GPS5
            {
                Lat = ByteUtil.ReadLong(ref content) / (double)elements[0],
                Lon = ByteUtil.ReadLong(ref content) / (double)elements[1],
                Alt = ByteUtil.ReadLong(ref content) / (double)elements[2],
                Speed2d = ByteUtil.ReadLong(ref content) / (double)elements[3],
                Speed3d = ByteUtil.ReadLong(ref content) / (double)elements[4],
            };

            pos += gpmf.StructSize;
            return result;
        }

        private GPS9 GetCoordValues9(GPMFStream gpmf, int[] elements, ref int pos)
        {
            var content = new ReadOnlySpan<byte>(gpmf.Content, pos + 8, gpmf.StructSize);
            var result = new GPS9
            {
                Lat = ByteUtil.ReadLong(ref content) / (double)elements[0],
                Lon = ByteUtil.ReadLong(ref content) / (double)elements[1],
                Alt = ByteUtil.ReadLong(ref content) / (double)elements[2],
                Speed2d = ByteUtil.ReadLong(ref content) / (double)elements[3],
                Speed3d = ByteUtil.ReadLong(ref content) / (double)elements[4],
                d2000 = ByteUtil.ReadLong(ref content),
                Seconds = ByteUtil.ReadLong(ref content) / (double)elements[6],
                DOP = ByteUtil.ReadShort(ref content) / (double)elements[7],
                Fix = ByteUtil.ReadShort(ref content)
            };

            pos += gpmf.StructSize;
            return result;
        }

        /// <summary>
        /// This reads the header information from the Mp4 metadata track and works out the size and data types that the 
        /// metadata stream contains
        /// </summary>
        private void ReadMetadata()
        {
            var reader = new SequentialStreamReader(_stream);

            do
            {
                long atomSize = reader.GetUInt32();
                string atomName = ByteUtil.IntToString(reader.GetUInt32());

                if (atomSize == 1)
                {
                    atomSize = checked((long)reader.GetUInt64());
                }

                if (allowedAtoms.Contains(atomName))
                {

                    switch (atomName)
                    {
                        case "mvhd": // movie header
                            {
                                reader.Skip(12);
                                ClockDaemon = reader.GetInt32();
                                ClockCount = reader.GetInt32();
                                reader.Skip(atomSize - 8 - 20); // skip over mvhd
                                Debug.WriteLine($"Processing Atom: {atomName}. ClockDaemon {ClockDaemon} ClockCount {ClockCount} ");
                                break;
                            }
                        case "trak": // track header
                            {
                                if (TrakNum + 1 < MAX_TRACKS)
                                    TrakNum++;
                                Debug.WriteLine($"Processing Atom: {atomName}. Track {TrakNum} ");
                                break;
                            }
                        case "mdhd": //media header
                            {
                                reader.Skip(4); // version 1byte + flags 3bytes
                                CreationTime = epoch.AddTicks(TimeSpan.TicksPerSecond * reader.GetUInt32());
                                ModificationTime = epoch.AddTicks(TimeSpan.TicksPerSecond * reader.GetUInt32());
                                TrakClockDaemon = reader.GetInt32();
                                TrakClockCount = reader.GetInt32();
                                reader.Skip(4); //language 2bytes + quality 2bytes

                                if (VideoLength == 0.0)
                                {
                                    VideoLength = (double)TrakClockCount / TrakClockDaemon;
                                }

                                Debug.WriteLine($"Processing Atom: {atomName}. VideoLength {VideoLength} seconds");
                                reader.Skip(atomSize - 8 - 24);

                                break;
                            }
                        case "hdlr":
                            {
                                reader.Skip(8);
                                trakType = ByteUtil.IntToString(reader.GetUInt32());
                                Debug.WriteLine($"Processing Atom: {atomName}. TrackType {trakType} ");
                                reader.Skip(atomSize - 8 - 12);
                                break;
                            }
                        case "edts": // edit list
                            {
                                int len;
                                uint elst, readnum;

                                reader.Skip(4);
                                elst = reader.GetUInt32();
                                Debug.WriteLine($"Processing Atom: {atomName}. ELST {elst} ");
                                len = 8;

                                if (ByteUtil.IntToString(elst) == "elst")
                                {
                                    int temp = reader.GetInt32();
                                    len += 4;

                                    if (temp == 0)
                                    {
                                        readnum = reader.GetUInt32();
                                        len += 4;

                                        if (readnum <= (atomSize / 12) && TrakClockDaemon > 0)
                                        {
                                            for (int i = 0; i < readnum; i++)
                                            {
                                                int segment_duration = reader.GetInt32();  // in MP4 clock base
                                                int segment_mediaTime = reader.GetInt32(); // in trak clock base
                                                int segment_mediaRate = reader.GetInt32(); // Fixed-point 65536 = 1.0X

                                                len += 12;

                                                if (segment_mediaTime == 0) // the segment_duration for blanked time
                                                {
                                                    TrakEditOffsets[TrakNum] += segment_duration;
                                                }
                                                else if (i == 0)
                                                {
                                                    TrakEditOffsets[TrakNum] -= (int)(segment_mediaTime / (double)TrakClockDaemon * ClockDaemon); //convert to MP4 clock base.
                                                }
                                            }

                                            if (trakType == "meta")
                                            {
                                                MetadataOffsetClockCount = TrakEditOffsets[TrakNum];
                                            }
                                        }
                                    }

                                    reader.Skip(atomSize - 8 - len);
                                }

                                break;
                            }
                        case "stsd": //sample description to determine the type of metadata
                            {
                                if (trakType == "meta")
                                {
                                    int len = 12;
                                    reader.Skip(len);
                                    trakSubType = ByteUtil.IntToString(reader.GetUInt32());
                                    len += 4;

                                    if (trakSubType != "gpmd")
                                    {
                                        trakType = string.Empty;
                                    }

                                    Debug.WriteLine($"Processing Atom: {atomName}. trakSubType {trakSubType} ");
                                    reader.Skip(atomSize - 8 - len);
                                }
                                else
                                {
                                    reader.Skip(atomSize - 8);
                                }

                                break;
                            }
                        case "stsc": // metadata stsc - offset chunks
                            {
                                if (trakType == "meta")
                                {
                                    int len;

                                    reader.Skip(4);
                                    MetaSTSCCount = reader.GetInt32();
                                    len = 8;

                                    if (MetaSTSCCount > 0)
                                    {
                                        int num = MetaSTSCCount;
                                        MetaSTSC = new List<SampleToChunk>();

                                        do
                                        {
                                            num--;
                                            MetaSTSC.Add(new SampleToChunk
                                            {
                                                ChunkNum = reader.GetUInt32(),
                                                Samples = reader.GetUInt32(),
                                                Id = reader.GetUInt32()
                                            });

                                            len += 12;
                                        }
                                        while (num > 0);
                                        Debug.WriteLine($"Processing Atom: {atomName}. MetaSTSC Count {MetaSTSC.Count} ");
                                    }

                                    reader.Skip(atomSize - 8 - len);
                                }
                                else
                                {
                                    reader.Skip(atomSize - 8);
                                }


                                break;
                            }
                        case "stsz": // sizes - GPMF byte size for each payload
                            {
                                if (trakType == "meta")
                                {
                                    int equalsamplesize, num, len;

                                    reader.Skip(4); // version 1byte, flags 3bytes
                                    equalsamplesize = reader.GetInt32(); // sample size

                                    SamplesCount = num = reader.GetInt32(); //number of entries

                                    len = 12;

                                    if (atomSize >= (20 + (num * sizeof(int))) || (equalsamplesize != 0 && atomSize == 20) && num > 0)
                                    {
                                        MetasizeCount = num;
                                        MetaSizes = new int[num];

                                        do
                                        {
                                            num--;
                                            MetaSizes[num] = reader.GetInt32();
                                            len += 4;
                                        }
                                        while (num > 0);

                                        Array.Reverse(MetaSizes);
                                        Debug.WriteLine($"Processing Atom: {atomName}. MetaSizes Count {MetaSizes.Count()} ");
                                    }
                                    else
                                    {
                                        do
                                        {
                                            num--;
                                            MetaSizes[num] = equalsamplesize;
                                        }
                                        while (num > 0);
                                        Debug.WriteLine($"Processing Atom: {atomName}. MetaSizes Count {MetaSizes.Count()} ");
                                    }

                                    reader.Skip(atomSize - 8 - len);
                                }
                                else
                                {
                                    reader.Skip(atomSize - 8);
                                }

                                break;
                            }
                        case "stco": // metadata stco - offsets
                            {
                                if (trakType == "meta")
                                {
                                    int len, num;

                                    reader.Skip(4);
                                    num = reader.GetInt32();
                                    Debug.WriteLine($"Processing Atom: {atomName}. num {num} ");
                                    len = 8;

                                    if (num <= ((atomSize - 8 - len) / sizeof(int)))
                                    {
                                        MetaSTSCCount = num;

                                        if (MetaSTSCCount > 0 && num != MetasizeCount && atomSize > (num * 4))
                                        {
                                            int fileoffset = 0, stsc_pos = 0, stco_pos = 0;

                                            MetaOffsets = new int[num];

                                            do
                                            {
                                                num--;
                                                MetaOffsets[num] = reader.GetInt32();
                                                len += 4;
                                            } while (num > 0);
                                            Debug.WriteLine($"Processing Atom: {atomName}. MetaOffsets {MetaOffsets.Count()} ");
                                            Array.Reverse(MetaOffsets);

                                            fileoffset = MetaOffsets[stco_pos];
                                            MetaOffsets[0] = fileoffset;

                                            num = 1;

                                            while (num < MetaSTSCCount)
                                            {
                                                if (num != MetaSTSC[stsc_pos].ChunkNum - 1 && 0 == (num - (MetaSTSC[stsc_pos].ChunkNum - 1)) % MetaSTSC[stsc_pos].Samples)
                                                {
                                                    stco_pos++;
                                                    fileoffset = MetaOffsets[stco_pos];
                                                }
                                                else
                                                {
                                                    fileoffset += MetaSizes[num - 1];
                                                }

                                                MetaOffsets[num] = fileoffset;

                                                num++;
                                            }
                                            Debug.WriteLine($"Processing Atom: {atomName}. MetaOffsets {MetaOffsets.Count()} ");
                                        }
                                        else if (num > 0 && MetasizeCount > 0 && MetaSizes.Length > 0 && atomSize > (num * 4))
                                        {
                                            MetaOffsets = new int[num];

                                            do
                                            {
                                                num--;
                                                MetaOffsets[num] = reader.GetInt32();
                                                len += 4;
                                            } while (num > 0);
                                            Debug.WriteLine($"Processing Atom: {atomName}. MetaOffsets {MetaOffsets.Count()} ");
                                            Array.Reverse(MetaOffsets);
                                        }
                                    }

                                    reader.Skip(atomSize - 8 - len);
                                }
                                else
                                {
                                    reader.Skip(atomSize - 8);
                                }

                                break;
                            }
                        case "stts": // Time-to-sample atoms
                            {
                                if (trakType == "meta")
                                {
                                    int len, num, entries;
                                    uint samples = 0, totalduration = 0;

                                    reader.Skip(4);
                                    num = reader.GetInt32();

                                    len = 8;

                                    if (num <= (atomSize / 8))
                                    {
                                        entries = num;

                                        MetaClockCount = TrakClockCount;
                                        MetaClockDaemon = TrakClockDaemon;

                                        while (entries > 0)
                                        {
                                            uint samplecount, duration;

                                            samplecount = reader.GetUInt32();
                                            duration = reader.GetUInt32();

                                            len += 8;

                                            samples += samplecount;
                                            totalduration += duration;
                                            entries--;

                                            MetadataLength += (double)(samplecount * (double)duration / MetaClockDaemon);

                                            if (samplecount > 1 || num == 1)
                                                BaseMetadataDuration = MetadataLength * MetaClockDaemon / samples;
                                        }
                                    }
                                    Debug.WriteLine($"Processing Atom: {atomName}. MetadataLength {MetadataLength} ");
                                    reader.Skip(atomSize - 8 - len);
                                }
                                else
                                {
                                    reader.Skip(atomSize - 8);
                                }

                                break;
                            }
                    }
                }
                else
                {
                    Debug.WriteLine("Skipping Atom: " + atomName);
                    reader.TrySkip(atomSize - 8);
                }

            }
            while (reader.Position <= _stream.Length && !reader.IsCloserToEnd(8));
        }

        private bool GetGPMF(GPMFStream stream)
        {
            if (stream != null && stream.Position + 1 < stream.Length)
            {
                uint bytes32 = ByteUtil.BytesToInt(stream.Content, stream.Position + 4);
                int GPMFType = (int)bytes32 & 0xff;
                int size = stream.DataSize(bytes32) >> 2;

                if (GPMFType == 0 && stream.NestLevel == 0 && stream.FourCC == DEVICE_DATA)
                {
                    stream.Position += 8;
                    stream.NestSize[stream.NestLevel] = size;
                }
                else
                {
                    if (size + 2 > stream.NestSize[stream.NestLevel])
                        return false;

                    if (GPMFType == 0)
                    {
                        stream.Position += 8;
                        stream.NestSize[stream.NestLevel] -= size + 2;
                        stream.NestLevel++;

                        if (stream.NestLevel > stream.NESTLIMIT)
                            return false;

                        stream.NestSize[stream.NestLevel] = size;
                    }
                    else
                    {
                        stream.Position += size * sizeof(int) + 8;
                        stream.NestSize[stream.NestLevel] -= size + 2;
                    }
                }

                while (stream.Position < stream.Length && stream.NestSize[stream.NestLevel] > 0 && stream.FourCC == "")
                {
                    stream.Position += 4;
                    stream.NestSize[stream.NestLevel]--;
                }

                while (stream.NestLevel > 0 && stream.NestSize[stream.NestLevel] == 0)
                {
                    stream.NestLevel--;
                }

                if (stream.Position < stream.Length)
                {
                    while (stream.Position + 4 < stream.Length && stream.NestSize[stream.NestLevel] > 0 && stream.FourCC == "")
                    {
                        stream.Position += 4;
                        stream.NestSize[stream.NestLevel]--;
                    }

                    if (stream.Position + 4 < stream.Length)
                    {
                        uint key = ByteUtil.BytesToInt(stream.Content, stream.Position);

                        if (!ByteUtil.IsValidFourCC(ByteUtil.FourCCFromInt(key)))
                        {
                            // Skip this nest level as the sizes within this level are corrupt.
                            return SkipLevel(stream);
                        }

                        if (stream.SampleSize(ByteUtil.BytesToInt(stream.Content, stream.Position + 4)) == 0)
                        {
                            // Skip this nest level as the sizes within this level are corrupt.
                            return SkipLevel(stream);
                        }

                        if (ByteUtil.FourCCFromInt(key) == DEVICE_ID && stream.Position + 8 < stream.Length)
                            stream.DeviceId = ByteUtil.HexToInt((int)ByteUtil.BytesToInt(stream.Content, stream.Position + 8));

                        if (ByteUtil.FourCCFromInt(key) == DEVICE_NAME)
                        {
                            if (stream.Position + 4 >= stream.Length)
                                return false;

                            size = stream.DataSize(ByteUtil.BytesToInt(stream.Content, stream.Position + 4));

                            if ((stream.Position + 4 + ((size + 3) >> 2)) >= stream.Length)
                                return false;

                            var bytes = stream.Content.Skip(stream.Position + 8).Take(size).ToArray();
                            stream.DeviceName = Encoding.UTF8.GetString(bytes).Replace("\0", "");
                        }
                    }
                    else
                    {
                        // end of buffer
                        return false;
                    }
                }
                else
                {
                    return false;
                }

                return true;
            }

            return false;
        }

        private bool GetNextGPMF(GPMFStream stream, string fourCC)
        {
            if (stream != null && stream.Position < stream.Length)
            {
                bool isNext = false;

                do
                {
                    isNext = GetGPMF(stream);
                    string cc = stream.FourCC;

                    //Console.WriteLine(cc);

                    if (cc == fourCC)
                    {
                        return true;
                    }
                }
                while (isNext);
            }

            return false;
        }

        private bool SkipLevel(GPMFStream stream)
        {
            if (stream != null)
            {
                stream.Position += stream.NestSize[stream.NestLevel];
                stream.NestSize[stream.NestLevel] = 0;

                while (stream.NestLevel > 0 && stream.NestSize[stream.NestLevel] == 0)
                    stream.NestLevel--;

                uint bytes32 = ByteUtil.BytesToInt(stream.Content, stream.Position + 8);
                int size = stream.DataSize(bytes32) >> 2;

                if (size <= 0)
                    return false;
                else
                    return true;
            }

            return false;
        }

        private int GetNumberOfPayloads
        {
            get { return SamplesCount; }
        }

        private int GetPayloadSize(int index)
        {
            if (SamplesCount > index)
            {
                return MetaSizes[index] & ~0x3;
            }

            return 0;
        }

        private void GetPayloadTime(int index, out double _in, out double _out)
        {
            _in = 0; _out = 0;

            if (MetaOffsets.Length > 0)
            {
                _in = index * (BaseMetadataDuration / MetaClockDaemon);
                _out = (index + 1) * BaseMetadataDuration / MetaClockDaemon;

                if (_out > MetadataLength)
                {
                    _out = MetadataLength;
                }

                _in += (double)MetadataOffsetClockCount / ClockDaemon;
                _out += (double)MetadataOffsetClockCount / ClockDaemon;
            }
        }

        private byte[] GetPayload(int index)
        {
            if (index < SamplesCount)
            {
                if ((_stream.Length >= MetaOffsets[index] + MetaSizes[index]) && MetaOffsets[index] > 0)
                {
                    int bufferSize = MetaSizes[index];
                    byte[] buffer = new byte[bufferSize];

                    _stream.Seek(MetaOffsets[index], SeekOrigin.Begin);
                    _stream.Read(buffer, 0, MetaSizes[index]);

                    return buffer;
                }
            }

            return null;
        }

        private DateTime? GPSUToUTCDate(string d)
        {
            string regex = @"(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})\.(\d{3})";
            int YEAR = 1, MONTH = 2, DAY = 3, HOUR = 4, MIN = 5, SEC = 6, MIL = 7;

            var parts = new Regex(regex).Match(d);

            if (parts.Success)
            {
                int year = int.Parse("20" + parts.Groups[YEAR].Value);
                int month = int.Parse(parts.Groups[MONTH].Value);
                int day = int.Parse(parts.Groups[DAY].Value);
                int hour = int.Parse(parts.Groups[HOUR].Value);
                int min = int.Parse(parts.Groups[MIN].Value);
                int sec = int.Parse(parts.Groups[SEC].Value);
                int mil = int.Parse(parts.Groups[MIL].Value);

                return new DateTime(year, month, day, hour, min, sec, mil, DateTimeKind.Utc);
            }

            return null;
        }
    }
}
