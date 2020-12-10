# FOVE Data Collector

## This repo is deprecated

The Unity Data Collector is renamed the "Gaze Recorder" and is bundled with the FOVE Unity Plugin, hence this repository has been deprecated.

You can find the source on GitHub [here](https://github.com/FoveHMD/UnityPlugin/blob/master/Assets/FoveUnityPlugin/Source/Behaviours/GazeRecorder.cs) or download the Unity package from [our website](https://fove-inc.com/downloads/).

## Original README

A Unity behaviour class for collecting and saving out CSV gaze data during a simulation.

This behaviour implements a lightweight collector of FOVE eye gaze data which writes your data asynchronously to the disk so you can record large amounts of data while minimizing the impact to your simulation's performance. Data is written out to a CSV file.

Essentially, two of the most expensive tasks in computing are writing data to disk and turning numbers into strings. This behaviour saves the Unity logic and rendering thread by offloading both processes to a separate thread.

It also includes some customization for how many precision digits you need when storing your data, what file name you want, etc...
