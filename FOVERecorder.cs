using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

// A behaviour class which records eye gaze data (with floating-point timestamps) and writes it out to a .csv file
// for continued processing.
public class FOVERecorder : MonoBehaviour
{
	// Require a reference (assigned via the Unity Inspector panel) to a FoveInterfaceBase object.
	// This could be either FoveInterface or FoveInterface2.
	[Tooltip("This should be a reference to any FoveInterface or FoveInterface2 object in the scene.")]
	public FoveInterfaceBase fove = null;

	// Pick a key (customizable via the Inspector panel) to toggle recording.
	[Tooltip("Pressing this key will toggle data recording.")]
	public KeyCode toggleRecordingKeyCode = KeyCode.Space;

	// The number a data to record before writing out to disk
	[Tooltip("The number of entries to store in memory before writing asynchronously to disk")]
	public uint writeAtDataCount = 1000;

	// The name of the file to write our results into
	[Tooltip("The base name of the file. Don't add any extensions, as \".csv\" will be appended to whatever you put " +
	         "here.")]
	public string fileName = "fove_recorded_results";

	// Check this to overwrite existing data files rather than incrementing a value each time.
	[Tooltip("If the specified filename already exists, the recorder will increment a counter until an unused " +
	         "filename is found.")]
	public bool overwriteExistingFile = false;

	[Serializable]
	public struct RecordingPrecision_struct
	{
		[Tooltip("How many digits of decimal precision to record")]
		public int timePrecision;

		[Tooltip("How many digits of decimal precision to use when writing vector data")]
		public int vectorPrecision;
		[Tooltip("Forces unused decimal precision to be written out with zeros, for instance, 4 rpecision digits " +
		         "and a value of 0.12 would be written \"0.1200\"")]
		public bool forcePrecisionDigits;
	}

	public RecordingPrecision_struct recordingPrecision  = new RecordingPrecision_struct
	{
		timePrecision = 10,
		vectorPrecision = 3,
		forcePrecisionDigits = false
	};

	//=================//
	// Private members //
	//=================//

	// Pricision format strings for converting numbers to strings in the CSV
	private string tPrecision;
	private string vPrecision;

	// An internal flag to track whether we should be recording or not
	private bool recordingStopped = true;

	// A struct for recording in one place all the information that needs to be recorded for each frame
	// If you need more data recorded, you can add more fields here. Just be sure to write is out as well later on.
	class RecordingDatum
	{
		public float frameTime;
		public Ray leftGaze;
		public Ray rightGaze;
	}

	// A list for storing the recorded data from many frames
	private List<RecordingDatum> dataSlice;

	// This reference to a list is used by the writing thread. Essentially, one list is being populated (above)
	// while another can be writing out to disk asynchronously (this one).
	private List<RecordingDatum> dataToWrite = null;

	// This mutex is used to prevent the main thread from changing the "dataToWrite" variable if it's currently
	// being used by the writing thread. This should not cause a conflict in most cases unless the write interval
	// is too large for the data being written out.
	private Mutex writingDataMutex = new Mutex(false);

	private EventWaitHandle threadWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);

	// Track whether or not the write thread should live.
	private bool threadShouldLive = true;

	// The thread object which we will call into the write thread function.
	private Thread writeThread;

	// Use this for initialization.
	void Start () {
		// Check to make sure that the FOVE interface variable is assigned. This prevents a ton of errors
		// from filling your log if you forget to assign the interface through the inspector.
		if (fove == null)
		{
			Debug.LogWarning("Forgot to assign a Fove interface to the FOVERecorder object.");
			enabled = false;
			return;
		}

		// We set the initial data slice capacity to the expected size + 1 so that we never waste time reallocating and
		// copying data under the hood. If the system ever requires more than a single extra entry, there is likely
		// a severe problem causing delays which should be addressed.
		dataSlice = new List<RecordingDatum>((int)(writeAtDataCount + 1));

		// If overwrite is not set, then we need to make sure our selected file name is valid before proceeding.
		{
			string testFileName = fileName + ".csv";
			if (!overwriteExistingFile)
			{
				int counter = 1;
				while (File.Exists(testFileName))
				{
					testFileName = fileName + "_" + (counter++) + ".csv"; // e.g., "results_12.csv"
				}
			}
			fileName = testFileName;

			Debug.Log("Writing data to " + fileName);
		}

		try {
			File.WriteAllText(fileName, "frameTime," +
			                            "leftGaze origin x,leftGaze origin y,leftGaze origin z," +
			                            "leftGaze direction x,leftGaze direction y,leftGaze direction z," +
			                            "rightGaze origin x,rightGaze origin y,rightGaze origin z," +
			                            "rightGaze direction x,rightGaze direction y,rightGaze direction z\n");
		} catch (Exception e) {
			Debug.LogError("Error writing header to output file:\n" + e);
			enabled = false;
			return;
		}

		// Setup the significant digits argument strings used when serializing numbers to text for the CSV
		char precisionChar = recordingPrecision.forcePrecisionDigits ? '0' : '#';
		vPrecision = "#0." + new string(precisionChar, recordingPrecision.vectorPrecision);
		tPrecision = "#0." + new string(precisionChar, recordingPrecision.timePrecision);

		// Coroutines give us a bit more control over when the call happens, and also simplify the code
		// structure. However they are only ever called once per frame -- they processing to happen in
		// pieces, but they shouldn't be confused with threads.
		StartCoroutine(RecordData());

		// Create the write thread to call "WriteThreadFunc", and then start it.
		writeThread = new Thread(WriteThreadFunc);
		writeThread.Start();
	}

	// Unity's standard Update function, here used only to listen for input to toggle data recording
	void Update()
	{
		// If you press the assigned key, it will toggle the "recordingStopped" variable.
		if (Input.GetKeyDown(toggleRecordingKeyCode))
		{
			recordingStopped = !recordingStopped;
			Debug.Log(recordingStopped ? "Stopping" : "Starting" + " data recording...");
		}
	}

	// This is called when the program quits, or when you press the stop button in the editor (if running from there).
	void OnApplicationQuit()
	{
		if (writeThread == null)
			return;

		// Get a lock to the mutex to make sure data isn't being written. Wait up to 200 milliseconds.
		if (writingDataMutex.WaitOne(200))
		{
			// Tell the thread to end, then release the mutex so it can finish.
			threadShouldLive = false;

			CheckForNullDataToWrite();
			dataToWrite = dataSlice;
			dataSlice = null;

			writingDataMutex.ReleaseMutex();

			if (!threadWaitHandle.Set())
				Debug.LogError("Error setting the event to wake up the file writer thread on application quit");
		}
		else
		{
			// If it times out, tell the operating system to abort the thread.
			writeThread.Abort();
		}

		// Wait for the write thrtead to end (up to 1 second).
		writeThread.Join(1000);
	}
	
	void CheckForNullDataToWrite()
	{
		// The write thread sets dataToWrite to null when it's done, so if it isn't null here, it's likely
		// that some major error occured.
		if (dataToWrite != null) {
			Debug.LogError("dataToWrite was not reset when it came time to set it; this could indicate a" +
			               "serious problem in the data recording/writing process.");
		}
	}

	// The coroutine function which records data to the dataSlice List<> member
	IEnumerator RecordData()
	{
		// Inifinite loops are okay within coroutines because the "yield" statement pauses the function each time to
		// return control to the main program. Great for breaking tasks up into smaller chunks over time, or for doing
		// small amounts of work each frame but potentially outside of the normal Update cycle/call order.
		while (true)
		{
			// This statement pauses this function until Unity has finished rendering a frame. Inside the while loop,
			// this means that this function will resume from here every frame.
			yield return new WaitForEndOfFrame();

			// If recording is stopped (which is it by default), loop back around next frame.
			if (recordingStopped)
				continue;

			// The FoveInterfaceBase.EyeRays struct contains world-space rays indicating eye gaze origin and direction,
			// so you don't necessarily need to record head position and orientation just to transform the gaze vectors
			// themselves. This data is pre-transformed for you.
			var rays = fove.GetGazeRays();

			// If you add new fields, be sure to write them here.
			RecordingDatum datum = new RecordingDatum
			{
				frameTime = Time.time,
				leftGaze = rays.left,
				rightGaze = rays.right
			};

			dataSlice.Add(datum);

			if (dataSlice.Count >= writeAtDataCount)
			{
				// Make sure we have exclusive access by locking the mutex, but only wait for up to 30 milliseconds.
				if (!writingDataMutex.WaitOne(30))
				{
					// If we got here, it means that we couldn't acquire exclusive access within the specified time
					// limit. Likely this means an error happened, but it could also mean that more data was being
					// written than it took to gather another set of data -- in which case you may need to extend the
					// timeout duration, though that will cause a noticeable frame skip in your application.

					// For now, the best thing we can do is continue the loop and try writing data again next frame.
					long excess = dataSlice.Count - writeAtDataCount;
					if (excess > 1)
						Debug.LogError("Data slice is " + excess + " entries over where it should be; this is" +
						               "indicative of a major performance concern in the data recording and writing" +
						               "process.");
					continue;
				}

				CheckForNullDataToWrite();

				// Move our current slice over to dataToWrite, and then create a new slice.
				dataToWrite = dataSlice;
				dataSlice = new List<RecordingDatum>((int)(writeAtDataCount + 1));

				// Release our claim on the mutex.
				writingDataMutex.ReleaseMutex();

				if (!threadWaitHandle.Set())
					Debug.LogError("Error setting the event to wake up the file writer thread");
			}
		}
	}

	private void WriteDataFromThread()
	{
		if (!writingDataMutex.WaitOne(10)) {
			Debug.LogWarning("Write thread couldn't lock mutex for 10ms, which is indicative of a problem where" +
			                 "the core loop is holding onto the mutex for too long, or may have not released the" +
			                 "mutex.");
			return;
		}

		if (dataToWrite != null) {
			Debug.Log("Writing " + dataToWrite.Count + " lines");
			try
			{
				string text = "";

				foreach (var datum in dataToWrite) {
					// This writes each element in the data list as a CSV-formatted line. Be sure to update this
					// (carefully) if you add or change around the data you're using.
					text += string.Format(
						"\"{0}\"," +
						"\"{1}\",\"{2}\",\"{3}\"," +
						"\"{4}\",\"{5}\",\"{6}\"," +
						"\"{7}\",\"{8}\",\"{9}\"," +
						"\"{10}\",\"{11}\",\"{12}\"\n",
						datum.frameTime.ToString(tPrecision),
						datum.leftGaze.origin.x.ToString(vPrecision),
						datum.leftGaze.origin.y.ToString(vPrecision),
						datum.leftGaze.origin.z.ToString(vPrecision),
						datum.leftGaze.direction.x.ToString(vPrecision),
						datum.leftGaze.direction.y.ToString(vPrecision),
						datum.leftGaze.direction.z.ToString(vPrecision),
						datum.rightGaze.origin.x.ToString(vPrecision),
						datum.rightGaze.origin.y.ToString(vPrecision),
						datum.rightGaze.origin.z.ToString(vPrecision),
						datum.rightGaze.direction.x.ToString(vPrecision),
						datum.rightGaze.direction.y.ToString(vPrecision),
						datum.rightGaze.direction.z.ToString(vPrecision));
				}

				File.AppendAllText(fileName, text);
			} catch (Exception e) {
				Debug.LogWarning("Exception writing to data file:\n" + e);
				threadShouldLive = false;
			}

			dataToWrite = null;
		}

		writingDataMutex.ReleaseMutex();
	}

	// This is the writing thread. By offloading file writing to a thread, we are less likely to impact peceived
	// performance inside the Unity game loop, and thus more likely to have accurate, consistent results.
	private void WriteThreadFunc()
	{
		while (threadShouldLive)
		{
			if (threadWaitHandle.WaitOne())
				WriteDataFromThread();
		}

		// Try to write one last time once the thread ends to catch any missed elements
		WriteDataFromThread();
	}
}
