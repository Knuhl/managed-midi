// FIXMEs:
// - some bad mappings:
//	- C int -> C# int
//	- C long -> C# int
// not sure what they should be.
// The sources are wrong. Those C code should not use int and long for each.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using PmDeviceID = System.Int32;
using PmTimestamp = System.Int32;
using PortMidiStream = System.IntPtr;
using PmMessage = System.Int32;
using PmError = Commons.Music.Midi.PortMidi.PortMidiErrorType;

namespace Commons.Music.Midi.PortMidi
{
	public class PortMidiDeviceManager
	{
		static PortMidiDeviceManager ()
		{
			PortMidiMarshal.Pm_Initialize ();
			#if !PORTABLE // FIXME: what to do for PCLs!?
			AppDomain.CurrentDomain.DomainUnload += delegate (object o, EventArgs e) {
				PortMidiMarshal.Pm_Terminate ();
			};
			#endif
		}

		public static int DeviceCount {
			get { return PortMidiMarshal.Pm_CountDevices (); }
		}

		public static int DefaultInputDeviceID {
			get { return PortMidiMarshal.Pm_GetDefaultInputDeviceID (); }
		}

		public static int DefaultOutputDeviceID {
			get { return PortMidiMarshal.Pm_GetDefaultOutputDeviceID (); }
		}

		public static IEnumerable<PortMidiDeviceInfo> AllDevices {
			get {
				for (int i = 0; i < DeviceCount; i++)
					yield return GetDeviceInfo (i);
			}
		}

		public static PortMidiDeviceInfo GetDeviceInfo (PmDeviceID id)
		{
			return new PortMidiDeviceInfo (id, PortMidiMarshal.Pm_GetDeviceInfo (id));
		}

		public static PortMidiInputStream OpenInput (PmDeviceID inputDevice)
		{
			return OpenInput (inputDevice, default_buffer_size);
		}

		const int default_buffer_size = 1024;

		public static PortMidiInputStream OpenInput (PmDeviceID inputDevice, int bufferSize)
		{
			IntPtr stream;
			var e = PortMidiMarshal.Pm_OpenInput (out stream, inputDevice, IntPtr.Zero, bufferSize, null, IntPtr.Zero);
			if (e != PmError.NoError)
				throw new PortMidiException (e, String.Format ("Failed to open MIDI input device {0}", e));
			return new PortMidiInputStream (stream, inputDevice);
		}

		public static PortMidiOutputStream OpenOutput (PmDeviceID outputDevice)
		{
			IntPtr stream;
			var e = PortMidiMarshal.Pm_OpenOutput (out stream, outputDevice, IntPtr.Zero, 0, null, IntPtr.Zero, 0);
			if (e != PmError.NoError)
				throw new PortMidiException (e, String.Format ("Failed to open MIDI output device {0}", e));
			return new PortMidiOutputStream (stream, outputDevice, 0);
		}
	}

	public enum PortMidiErrorType
	{
		NoError = 0,
		NoData = 0,
		GotData = 1,
		HostError = -10000,
		InvalidDeviceId,
		InsufficientMemory,
		BufferTooSmall,
		BufferOverflow,
		BadPointer,
		BadData,
		InternalError,
		BufferMaxSize,
	}

	public class PortMidiException : Exception
	{
		PmError error_type;

		public PortMidiException (PmError errorType, string message)
			: this (errorType, message, null)
		{
		}

		public PortMidiException (PmError errorType, string message, Exception innerException)
			: base (message, innerException)
		{
			error_type = errorType;
		}

		public PmError ErrorType {
			get { return error_type; }
		}
	}

	public struct PortMidiDeviceInfo
	{
		int id;
		PmDeviceInfo info;

		internal PortMidiDeviceInfo (int id, IntPtr ptr)
		{
			this.id = id;
			this.info = (PmDeviceInfo) Marshal.PtrToStructure (ptr, typeof (PmDeviceInfo));
		}

		public int ID {
			get { return id; }
			set { id = value; }
		}

		public string Interface {
			get { return Marshal.PtrToStringAnsi (info.Interface); }
		}

		public string Name {
			get { return Marshal.PtrToStringAnsi (info.Name); }
		}

		public bool IsInput { get { return info.Input != 0; } }
		public bool IsOutput { get { return info.Output != 0; } }
		public bool IsOpened { get { return info.Opened != 0; } }

		public override string ToString ()
		{
			return String.Format ("{0} - {1} ({2} {3})", Interface, Name, IsInput ? (IsOutput ? "I/O" : "Input") : (IsOutput ? "Output" : "N/A"), IsOpened ? "open" : String.Empty);
		}
	}

	public abstract class PortMidiStream : IDisposable
	{
		public static IEnumerable<PortMidiEvent> Convert (byte [] bytes, int index, int size)
		{
			int i = index;
			int end = index + size;
			while (i < end) {
				if (bytes [i] == 0xF0 || bytes [i] == 0xF7) {
					var tmp = new byte [size];
					Array.Copy (bytes, i, tmp, 0, tmp.Length);
					yield return new PortMidiEvent () {Message = new PortMidiMessage (0xF0, 0, 0), Data = tmp};
					i += size + 1;
				} else {
					if (end < i + 3)
						throw new PortMidiException (PortMidiErrorType.NoError, string.Format ("Received data was incomplete to build MIDI status message for '{0:X}' status.", bytes [i]));
					yield return new PortMidiEvent () {Message = new PortMidiMessage (bytes [i], bytes [i + 1], bytes [i + 2])};
					i += 3;
				}
			}
		}

		internal IntPtr stream;
		internal PmDeviceID device;

		protected PortMidiStream (IntPtr stream, PmDeviceID deviceID)
		{
			this.stream = stream;
			device = deviceID;
		}

		public void Abort ()
		{
			PortMidiMarshal.Pm_Abort (stream);
		}

		public void Close ()
		{
			Dispose ();
		}

		public void Dispose ()
		{
			PortMidiMarshal.Pm_Close (stream);
		}

		public void SetFilter (PortMidiFilter filters)
		{
			PortMidiMarshal.Pm_SetFilter (stream, filters);
		}

		public void SetChannelMask (int mask)
		{
			PortMidiMarshal.Pm_SetChannelMask (stream, mask);
		}
	}

	public class PortMidiInputStream : PortMidiStream
	{
		public PortMidiInputStream (IntPtr stream, PmDeviceID inputDevice)
			: base (stream, inputDevice)
		{
		}

		public bool HasData {
			get { return PortMidiMarshal.Pm_Poll (stream) == PortMidiErrorType.GotData; }
		}

		public int Read (byte [] buffer, int index, int length)
		{
			var gch = GCHandle.Alloc (buffer);
			try {
				var ptr = Marshal.UnsafeAddrOfPinnedArrayElement (buffer, index);
				int size = PortMidiMarshal.Pm_Read (stream, ptr, length);
				if (size < 0)
					throw new PortMidiException ((PortMidiErrorType) size, PortMidiMarshal.Pm_GetErrorText ((PmError) size));
				return size * 4;
			} finally {
				gch.Free ();
			}
		}
	}

	public class PortMidiOutputStream : PortMidiStream
	{
		public PortMidiOutputStream (IntPtr stream, PmDeviceID outputDevice, int latency)
			: base (stream, outputDevice)
		{
		}

		public void Write (PortMidiEvent mevent)
		{
			if (mevent.Data != null)
				WriteSysEx (mevent.Timestamp, mevent.Data);
			else
				Write (mevent.Timestamp, mevent.Message);
		}

		public void Write (PmTimestamp when, PortMidiMessage msg)
		{
			var ret = PortMidiMarshal.Pm_WriteShort (stream, when, msg);
			if (ret != PmError.NoError)
				throw new PortMidiException (ret, String.Format ("Failed to write message {0} : {1}", msg.Value, PortMidiMarshal.Pm_GetErrorText ((PmError) ret)));
		}

		public void WriteSysEx (PmTimestamp when, byte [] sysex)
		{
			var ret = PortMidiMarshal.Pm_WriteSysEx (stream, when, sysex);
			if (ret != PmError.NoError)
				throw new PortMidiException (ret, String.Format ("Failed to write sysex message : {0}", PortMidiMarshal.Pm_GetErrorText ((PmError) ret)));
		}

		public void Write (PortMidiEvent [] buffer)
		{
			Write (buffer, 0, buffer.Length);
		}

		public void Write (PortMidiEvent [] buffer, int index, int length)
		{
			var gch = GCHandle.Alloc (buffer);
			try {
				var ptr = Marshal.UnsafeAddrOfPinnedArrayElement (buffer, index);
				var ret = PortMidiMarshal.Pm_Write (stream, ptr, length);
				if (ret != PmError.NoError)
					throw new PortMidiException (ret, String.Format ("Failed to write messages : {0}", PortMidiMarshal.Pm_GetErrorText ((PmError) ret)));
			} finally {
				gch.Free ();
			}
		}
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct PortMidiEvent
	{
		PortMidiMessage msg;
		PmTimestamp ts;
		#if !PORTABLE // FIXME: wait, P/Invoke exists without [NonSerialized]!?
		[NonSerialized]
		#endif
		byte [] data;

		public PortMidiMessage Message {
			get { return msg; }
			set { msg = value; }
		}

		public PmTimestamp Timestamp {
			get { return ts; }
			set { ts = value; }
		}

		public byte [] Data {
			get { return data; }
			set { data = value; }
		}
	}

	public struct PortMidiMessage
	{
		PmMessage v;

		public PortMidiMessage (PmMessage value)
		{
			v = value;
		}

		public PortMidiMessage (int status, int data1, int data2)
		{
			v = ((((data2) << 16) & 0xFF0000) | (((data1) << 8) & 0xFF00) | ((status) & 0xFF)); 
		}

		public PmMessage Value {
			get { return v; }
		}
	}

	public delegate PmTimestamp MidiTimeProcDelegate (IntPtr timeInfo);

	[Flags]
	public enum PortMidiFilter : int
	{
		Active = 1 << 0x0E,
		SysEx = 1 << 0x00,
		Clock = 1 << 0x08,
		Play = ((1 << 0x0A) | (1 << 0x0C) | (1 << 0x0B)),
		Tick = (1 << 0x09),
		FD = (1 << 0x0D),
		Undefined = FD,
		Reset = (1 << 0x0F),
		RealTime = (Active | SysEx | Clock | Play | Undefined | Reset | Tick),
		Note = ((1 << 0x19) | (1 << 0x18)),
		CAF = (1 << 0x1D),
		PAF = (1 << 0x1A),
		AF = (CAF | PAF),
		Program = (1 << 0x1C),
		Control = (1 << 0x1B),
		PitchBend = (1 << 0x1E),
		MTC = (1 << 0x01),
		SongPosition = (1 << 0x02),
		SongSelect = (1 << 0x03),
		Tune = (1 << 0x06),
		SystemCommon = (MTC | SongPosition | SongSelect | Tune)
	}

	// Marshal types

	class PortMidiMarshal
	{
		[DllImport ("portmidi")]
		public static extern PmError Pm_Initialize ();

		[DllImport ("portmidi")]
		public static extern PmError Pm_Terminate ();

		// TODO
		[DllImport ("portmidi")]
		public static extern int Pm_HasHostError (IntPtr stream);

		// TODO
		[DllImport ("portmidi")]
		public static extern string Pm_GetErrorText (PmError errnum);

		// TODO
		[DllImport ("portmidi")]
		public static extern void Pm_GetHostErrorText (IntPtr msg, uint len);

		const int HDRLENGTH = 50;
		const uint PM_HOST_ERROR_MSG_LEN = 256;

		// Device enumeration

		const PmDeviceID PmNoDevice = -1;

		[DllImport ("portmidi")]
		public static extern int Pm_CountDevices ();

		[DllImport ("portmidi")]
		public static extern PmDeviceID Pm_GetDefaultInputDeviceID ();

		[DllImport ("portmidi")]
		public static extern PmDeviceID Pm_GetDefaultOutputDeviceID ();

		[DllImport ("portmidi")]
		public static extern IntPtr Pm_GetDeviceInfo (PmDeviceID id);

		[DllImport ("portmidi")]
		public static extern PmError Pm_OpenInput (
			out IntPtr stream,
			PmDeviceID inputDevice,
			IntPtr inputDriverInfo,
			int bufferSize,
			MidiTimeProcDelegate timeProc,
			IntPtr timeInfo);

		[DllImport ("portmidi")]
		public static extern PmError Pm_OpenOutput (
			out IntPtr stream,
			PmDeviceID outputDevice,
			IntPtr outputDriverInfo,
			int bufferSize,
			MidiTimeProcDelegate time_proc,
			IntPtr time_info,
			int latency);

		[DllImport ("portmidi")]
		public static extern PmError Pm_SetFilter (IntPtr stream, PortMidiFilter filters);

		// TODO
		public static int Pm_Channel (int channel) { return 1 << channel; }

		[DllImport ("portmidi")]
		public static extern PmError Pm_SetChannelMask (IntPtr stream, int mask);

		[DllImport ("portmidi")]
		public static extern PmError Pm_Abort (IntPtr stream);

		[DllImport ("portmidi")]
		public static extern PmError Pm_Close (IntPtr stream);

		// TODO
		public static int Pm_MessageStatus (int msg) { return ((msg) & 0xFF); }
		// TODO
		public static int Pm_MessageData1 (int msg) { return (((msg) >> 8) & 0xFF); }
		// TODO
		public static int Pm_MessageData2 (int msg) { return (((msg) >> 16) & 0xFF); }

		[DllImport ("portmidi")]
		public static extern int Pm_Read (IntPtr stream, IntPtr buffer, int length);

		[DllImport ("portmidi")]
		public static extern PmError Pm_Poll (IntPtr stream);

		[DllImport ("portmidi")]
		public static extern PmError Pm_Write (IntPtr stream, IntPtr buffer, int length);

		[DllImport ("portmidi")]
		public static extern PmError Pm_WriteShort (IntPtr stream, PmTimestamp when, PortMidiMessage msg);

		[DllImport ("portmidi")]
		public static extern PmError Pm_WriteSysEx (IntPtr stream, PmTimestamp when, byte [] msg);
	}

	[StructLayout (LayoutKind.Sequential)]
	struct PmDeviceInfo
	{
		[MarshalAs (UnmanagedType.SysInt)]
		public int StructVersion; // it is not actually used.
		public IntPtr Interface; // char*
		public IntPtr Name; // char*
		[MarshalAs (UnmanagedType.SysInt)]
		public int Input; // 1 or 0
		[MarshalAs (UnmanagedType.SysInt)]
		public int Output; // 1 or 0
		[MarshalAs (UnmanagedType.SysInt)]
		public int Opened;

		public override string ToString ()
		{
			return String.Format ("{0},{1:X},{2:X},{3},{4},{5}", StructVersion, Interface, Name, Input, Output, Opened);
		}
	}
}

