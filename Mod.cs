
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using DevConsole;
using DevConsole.Commands;
namespace Breadway;

[BepInEx.BepInPlugin("thalber.breadway", "Breadway", "0.1")]
public class Mod : BepInEx.BaseUnityPlugin
{
	const string HELP_STRING = """
    breadway status
    breadway list [all|ok|err|running]
    breadway clear [all|ok|err|running]
    breadway details rooomname
    breadway remove rooomname
    breadway locks [list|clear]
    """;
	const string HELP_SELECTORS = "all|ok|err|running";
	const string SEL_ALL = "all";
	const string SEL_OK = "ok";
	const string SEL_ERR = "err";
	const string SEL_RUNNING = "running";
	private readonly static string[] all_selectors = new[] { SEL_ALL, SEL_OK, SEL_ERR, SEL_RUNNING };
	public static Mod inst = null!;
	public static BepInEx.Logging.ManualLogSource logger => inst.Logger;
	public void OnEnable()
	{
		inst = this;
		try
		{
			Register();
		}
		catch (Exception ex)
		{
			logger.LogFatal(ex);
		}
	}
	public void Update()
	{
		foreach ((var room, var taskInfo) in AllTasks)
		{
			if (taskInfo.task.IsCompleted)
			{
				ActiveTasks.Remove(room);
				var selectedDictionary = taskInfo.task.Exception is not null ? FailedTasks : CompletedTasks;
				selectedDictionary[room] = taskInfo;
			}
		}
	}

	internal static void Register()
	{
		On.WorldLoader.LoadAbstractRoom += WL_LAbsRoomHk;
		ThreadPool.GetMaxThreads(out int oldwt, out int oldSecThr);
		ThreadPool.SetMaxThreads(Environment.ProcessorCount, oldSecThr);
		logger.LogMessage($"Thread limit: {Environment.ProcessorCount}");
		new CommandBuilder()
			.Name("breadway")
			.Run((args) =>
			{
				switch (args)
				{
				case ["status"]:
				{
					DevConsole.GameConsole.WriteLine($"Breadway running {ActiveTasks.Count} success {CompletedTasks.Count} failed {FailedTasks.Count}");
					break;
				}
				case ["list", string arg]:
				{
					Dictionary<string, TaskInfo>? selectedDict = SelectDictionary(arg);
					if (selectedDict is null)
					{
						DevConsole.GameConsole.WriteLine(HELP_SELECTORS);
						break;
					}
					foreach ((var room, var task) in selectedDict)
					{
						DevConsole.GameConsole.WriteLine(TaskQuickDesc(room, task));
					}
					break;
				}
				case ["clear", string arg]:
				{
					var selectedDict = SelectDictionary(arg);
					if (selectedDict is null)
					{
						DevConsole.GameConsole.WriteLine(HELP_SELECTORS);
						break;
					}
					var rooms = selectedDict.Keys.ToArray();
					foreach (string room in rooms) RemoveTaskForRoom(room);
					break;
				}
				case ["details", string arg]:
				{
					if (!AllTasks.TryGetValue(arg, out TaskInfo taskInfo))
					{
						GameConsole.WriteLine("Task not found");
					}
					GameConsole.WriteLine($"{arg}'s status:");
					GameConsole.WriteLine(TaskQuickDesc(arg, taskInfo));
					GameConsole.WriteLine(taskInfo.task.Exception.StackTrace);

					break;
				}
				case ["remove", string arg]:
				{
					if (!AllTasks.TryGetValue(arg, out TaskInfo task))
					{
						GameConsole.WriteLine("Task not found");
					}
					RemoveTaskForRoom(arg);
					break;
				}
				case ["locks", string arg]:
				{
					switch (arg)
					{
					case "list":
					{
						lock (RoomLocks)
						{
							GameConsole.WriteLine(RoomLocks.Aggregate("", (str, newroom) => $"{str}, {newroom}"));
						}
						break;
					}
					case "clear":
					{
						lock (RoomLocks)
						{
							RoomLocks.Clear();
						}
						break;
					}
					default:
					{
						GameConsole.WriteLine("breadway locks [list|clear]");
						break;
					}
					}
					break;
				}
				default:
				{
					DevConsole.GameConsole.WriteLine(HELP_STRING);
					break;
				}
				}
			})
			.AutoComplete((args) =>
			{
				return args switch
				{
					[] => new[] { "status", "list", "clear", "details" },
					["list"] => all_selectors,
					["clear"] => all_selectors,
					["locks"] => new[] { "list", "clear" },
					_ => new string[0]
				};
			})
			.Register();
	}

	private static void WL_LAbsRoomHk(On.WorldLoader.orig_LoadAbstractRoom orig, World world, string roomName, AbstractRoom room, RainWorldGame.SetupValues setupValues)
	{
		var tarFile = WorldLoader.FindRoomFile(roomName, false, ".txt");
		var levelLines = File.ReadAllLines(tarFile);
		bool NeedBake = RoomPreprocessor.VersionFix(ref levelLines) || (int.Parse(levelLines[9].Split(new char[] { '|' })[0]) < world.preProcessingGeneration);
		var origDB = setupValues.dontBake;
		setupValues.dontBake = true;
		orig(world, roomName, room, setupValues);
		//setupValues.dontBake = true;
		if (NeedBake)
		{
			//Task t2 = new()
			CancellationTokenSource ctSource = new();
			Task t = new(() =>
			{
				BakeRoom(
				room,
				levelLines,
				world,
				setupValues,
				world.preProcessingGeneration,
				tarFile,
				ctSource);
			}, new CancellationToken());
			ActiveTasks.Add(room.name, new(t, ctSource));
			AllTasks.Add(room.name, new(t, ctSource));
		}
	}
	internal static void BakeRoom(
		AbstractRoom rm,
		string[] leveltext,
		World world,
		RainWorldGame.SetupValues sval,
		int ppg,
		string tarFile,
		CancellationTokenSource ctSource)
	{
		CancellationToken token = ctSource.Token;
		token.ThrowIfCancellationRequested();
		lock (RoomLocks)
		{
			if (RoomLocks.Contains(rm.name))
			{
				throw new Exception($"{rm.name} is already queued for baking; skipping bake");
			}
			logger.LogMessage($"{DateTime.Now} : Queued baking of room {rm.name}");
			logger.LogMessage($"Current thread: {Thread.CurrentThread.ManagedThreadId}");
			RoomLocks.Add(rm.name);
		}
		sval.dontBake = false;
		string[]? res = RoomPreprocessor.PreprocessRoom(rm, leveltext, world, sval, ppg);
		File.WriteAllLines(tarFile, res);
		lock (RoomLocks) RoomLocks.Remove(rm.name);
		logger.LogMessage($"{DateTime.Now} : Baking of {rm.name} finished, result saved:\n{tarFile}");
	}

	internal static void RemoveTaskForRoom(string roomName)
	{
		if (AllTasks.TryGetValue(roomName, out TaskInfo task))
		{

		}
		FailedTasks.Remove(roomName);
		CompletedTasks.Remove(roomName);
		ActiveTasks.Remove(roomName);
		AllTasks.Remove(roomName);
	}
	private static Dictionary<string, TaskInfo>? SelectDictionary(string arg)
	{
		return arg switch
		{
			SEL_ALL => AllTasks,
			SEL_ERR => FailedTasks,
			SEL_OK => CompletedTasks,
			SEL_RUNNING => ActiveTasks,
			_ => null
		};
	}
	public record TaskInfo(Task task, CancellationTokenSource ctSource);
	internal static string TaskQuickDesc(string roomName, TaskInfo taskInfo)
		=> $"{roomName} : status - {(taskInfo.task.IsCompleted ? "completed" : "running")} : error - {(taskInfo.task.Exception is Exception ex ? ex.Message : "None")}";
	internal static Dictionary<string, TaskInfo> FailedTasks = new(StringComparer.InvariantCultureIgnoreCase);
	internal static Dictionary<string, TaskInfo> CompletedTasks = new(StringComparer.InvariantCultureIgnoreCase);
	internal static Dictionary<string, TaskInfo> ActiveTasks = new(StringComparer.InvariantCultureIgnoreCase);
	internal static Dictionary<string, TaskInfo> AllTasks = new(StringComparer.InvariantCultureIgnoreCase);
	internal static HashSet<string> RoomLocks = new();
}
