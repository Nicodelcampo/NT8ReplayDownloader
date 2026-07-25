// NinjaTrader 8 Add-On — Market Replay bulk downloader with a retention probe.
//
// Drives the same undocumented API the Historical Data window uses to fetch one
// day of market replay data:
//
//     NinjaTrader.Server.HdsClient.RequestMarketReplay(
//         Instrument, DateTime dateEst, Action<ErrorCode,string,object>, IProgress, object)
//
// The method is public, but the HdsClient instance hangs off Connection via an
// internal property, so it is reached by reflection. NinjaTrader.Core.dll is
// obfuscated (AgileDotNet) — signatures survive, method bodies do not, so the
// behaviour below was derived from signatures plus observed results, not source.
//
// Install: copy to Documents\NinjaTrader 8\bin\Custom\AddOns\ and compile in the
// NinjaScript Editor (F5). Opens under Control Center -> Tools -> Replay Downloader.

#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.Server;
#endregion

namespace NinjaTrader.Gui.NinjaScript
{
	public class ReplayDownloaderAddOn : AddOnBase
	{
		private NTMenuItem menuItem;
		private NTMenuItem toolsMenuItem;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name        = "Replay Downloader";
				Description = "Bulk-downloads NT8 market replay days and probes how far back the server serves them.";
			}
		}

		protected override void OnWindowCreated(Window window)
		{
			ControlCenter cc = window as ControlCenter;
			if (cc == null)
				return;

			toolsMenuItem = cc.FindFirst("ControlCenterMenuItemTools") as NTMenuItem;
			if (toolsMenuItem == null)
				return;

			menuItem = new NTMenuItem
			{
				Header = "Replay Downloader",
				Style  = Application.Current.TryFindResource("MainMenuItem") as Style
			};
			toolsMenuItem.Items.Add(menuItem);
			menuItem.Click += OnMenuItemClick;
		}

		protected override void OnWindowDestroyed(Window window)
		{
			if (menuItem == null || !(window is ControlCenter))
				return;

			menuItem.Click -= OnMenuItemClick;
			if (toolsMenuItem != null && toolsMenuItem.Items.Contains(menuItem))
				toolsMenuItem.Items.Remove(menuItem);
			menuItem      = null;
			toolsMenuItem = null;
		}

		private void OnMenuItemClick(object sender, RoutedEventArgs e)
		{
			Globals.RandomDispatcher.BeginInvoke(new Action(() => new ReplayDownloaderWindow().Show()));
		}
	}

	// The obfuscated internals call into IProgress; a null would risk an NRE inside
	// code we cannot read, so hand it a no-op that also carries the abort flag.
	internal class NoProgress : NinjaTrader.Core.IProgress
	{
		private bool aborted;

		public bool IsAborted { get { return aborted; } }
		public string Message { get; set; }
		public event EventHandler Aborted;

		public void Abort()
		{
			aborted = true;
			EventHandler h = Aborted;
			if (h != null)
				h(this, EventArgs.Empty);
		}

		public void PerformStep() { }
		public void SetUp(long maxSteps, bool isAbortable) { }
		public void TearDown() { }
	}

	public class ReplayDownloaderWindow : NTWindow, IWorkspacePersistence
	{
		private const string NoDepth = "NO DEPTH";

		// The server answers a day it does not have with a clear
		// "no market replay data available" — cheaper and more honest than any
		// guess at the retention window.
		private const string NoDataMarker      = "no market replay data";
		private const int    NoDataStreakToStop = 5;

		// CME equity-index full closures. A wrong entry only costs one skipped
		// request, so this list stays short rather than exhaustive.
		private static readonly HashSet<DateTime> Holidays = new HashSet<DateTime>
		{
			new DateTime(2025, 12, 25), new DateTime(2026, 1, 1),
			new DateTime(2026, 4, 3),   new DateTime(2026, 5, 25),
			new DateTime(2026, 6, 19),  new DateTime(2026, 7, 3),
			new DateTime(2026, 9, 7)
		};

		private TextBox tbSymbols;
		private TextBox tbFrom;
		private TextBox tbTo;
		private TextBox tbDelay;
		private TextBox tbLog;
		private Button  bProbe;
		private Button  bDownload;
		private Button  bStop;

		private volatile bool running;
		private volatile bool cancelling;

		public ReplayDownloaderWindow()
		{
			Caption = "Replay Downloader";
			Width   = 620;
			Height  = 560;
			Content = BuildContent();
			Loaded += (o, e) =>
			{
				if (WorkspaceOptions == null)
					WorkspaceOptions = new WorkspaceOptions("ReplayDownloader-" + Guid.NewGuid().ToString("N"), this);
			};
		}

		protected override void OnClosed(EventArgs e)
		{
			cancelling = true;
			base.OnClosed(e);
		}

		#region UI

		private DependencyObject BuildContent()
		{
			double m = (double)FindResource("MarginBase");
			Grid grid = new Grid { Margin = new Thickness(m) };
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

			StackPanel form = new StackPanel();
			tbSymbols = AddField(form, m, "Root symbols (semicolon separated) — the front-month contract is resolved per date:", "NQ;MNQ");
			tbFrom    = AddField(form, m, "From (yyyy-MM-dd) — optional floor. Downloads run newest-first and stop on their own when the server runs out of data:", "");
			tbTo      = AddField(form, m, "To (yyyy-MM-dd):", DateTime.Now.Date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
			tbDelay   = AddField(form, m, "Seconds to wait between requests:", "3");
			Grid.SetRow(form, 0);
			grid.Children.Add(form);

			StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(m, m, m, 0) };
			bProbe    = new Button { Content = "_Probe retention", Margin = new Thickness(0, 0, m, 0), Padding = new Thickness(m, 2, m, 2) };
			bDownload = new Button { Content = "_Download missing", Margin = new Thickness(0, 0, m, 0), Padding = new Thickness(m, 2, m, 2) };
			bStop     = new Button { Content = "_Stop", IsEnabled = false, Padding = new Thickness(m, 2, m, 2) };
			bProbe.Click    += (o, e) => Start(true);
			bDownload.Click += (o, e) => Start(false);
			bStop.Click     += (o, e) => { cancelling = true; Log("-- stop requested, finishing current day --"); };
			buttons.Children.Add(bProbe);
			buttons.Children.Add(bDownload);
			buttons.Children.Add(bStop);
			Grid.SetRow(buttons, 1);
			grid.Children.Add(buttons);

			tbLog = new TextBox
			{
				Margin               = new Thickness(m),
				IsReadOnly           = true,
				AcceptsReturn        = true,
				VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				FontFamily           = new FontFamily("Consolas")
			};
			Grid.SetRow(tbLog, 2);
			grid.Children.Add(tbLog);

			return grid;
		}

		private TextBox AddField(Panel parent, double m, string label, string value)
		{
			parent.Children.Add(new Label
			{
				Content    = label,
				Foreground = FindResource("FontLabelBrush") as Brush,
				Margin     = new Thickness(m, m, m, 0)
			});
			TextBox tb = new TextBox { Text = value, Margin = new Thickness(m, 0, m, 0) };
			parent.Children.Add(tb);
			return tb;
		}

		private void Log(string line)
		{
			Dispatcher.InvokeAsync(new Action(() =>
			{
				tbLog.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
				tbLog.ScrollToEnd();
			}));
		}

		private void SetRunning(bool on)
		{
			running = on;
			Dispatcher.InvokeAsync(new Action(() =>
			{
				bProbe.IsEnabled    = !on;
				bDownload.IsEnabled = !on;
				bStop.IsEnabled     = on;
			}));
		}

		#endregion

		#region Contract / calendar helpers

		private static DateTime ThirdFriday(int year, int month)
		{
			DateTime first = new DateTime(year, month, 1);
			int offset = ((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7;
			return first.AddDays(offset + 14);
		}

		// CME equity-index roll = 8 days before expiry. Verified against recorded
		// tick data: NQ 06-26 -> 09-26 crossed on 2026-06-11, which this reproduces.
		public static string FrontMonth(DateTime date)
		{
			int[] months = { 3, 6, 9, 12 };
			for (int y = date.Year; y <= date.Year + 1; y++)
				foreach (int mo in months)
				{
					DateTime roll = ThirdFriday(y, mo).AddDays(-8);
					if (roll > date.Date)
						return string.Format(CultureInfo.InvariantCulture, "{0:00}-{1:00}", mo, y % 100);
				}
			return null;
		}

		private static bool IsSession(DateTime d)
		{
			return d.DayOfWeek != DayOfWeek.Saturday
				&& d.DayOfWeek != DayOfWeek.Sunday
				&& !Holidays.Contains(d.Date);
		}

		private static List<DateTime> Sessions(DateTime from, DateTime to)
		{
			List<DateTime> days = new List<DateTime>();
			for (DateTime d = from.Date; d <= to.Date; d = d.AddDays(1))
				if (IsSession(d))
					days.Add(d);
			return days;
		}

		private static string ReplayPath(string instrumentName, DateTime date)
		{
			return Path.Combine(Globals.UserDataDir, "db", "replay", instrumentName,
				date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".nrd");
		}

		private static bool IsNoData(string detail)
		{
			return detail != null && detail.IndexOf(NoDataMarker, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		// Reads the 44x80-byte header. Slots 10/11 are L2 Ask/Bid; each slot is
		// {double last, int count, 5x double, int flag, long t0, long t1, long volumeSum}.
		// count and volumeSum are the fields a full stream decode checksums against,
		// so mean depth size is trustworthy without touching the event stream.
		private static string DepthSummary(string path)
		{
			try
			{
				byte[] h = new byte[44 * 80];
				using (FileStream fs = File.OpenRead(path))
					if (fs.Read(h, 0, h.Length) < h.Length)
						return "truncated header";

				long events = 0, volume = 0;
				for (int slot = 10; slot <= 11; slot++)
				{
					events += BitConverter.ToInt32(h, slot * 80 + 8);
					volume += BitConverter.ToInt64(h, slot * 80 + 72);
				}
				if (events == 0)
					return NoDepth;
				return string.Format(CultureInfo.InvariantCulture, "{0:N0} L2 events, mean size {1:F2}",
					events, (double)volume / events);
			}
			catch (Exception ex)
			{
				return "unreadable: " + ex.Message;
			}
		}

		#endregion

		#region HDS access

		private static HdsClient GetHdsClient()
		{
			PropertyInfo pi = typeof(Connection).GetProperty("HistoricalDataClient",
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (pi == null)
				return null;

			Connection client = Connection.ClientConnection;
			if (client != null)
			{
				HdsClient hds = pi.GetValue(client, null) as HdsClient;
				if (hds != null)
					return hds;
			}
			foreach (Connection c in Connection.Connections)
			{
				HdsClient hds = pi.GetValue(c, null) as HdsClient;
				if (hds != null)
					return hds;
			}
			return null;
		}

		// Blocking wrapper. The request is issued on NT's dispatcher (the Historical
		// Data window issues it from the UI thread) while this worker thread waits.
		private bool RequestDay(HdsClient hds, Instrument instrument, DateTime dateEst, out string message)
		{
			ErrorCode code = ErrorCode.NoError;
			string    text = null;
			string    fail = null;

			using (ManualResetEventSlim done = new ManualResetEventSlim(false))
			{
				Action<ErrorCode, string, object> callback = (c, t, s) =>
				{
					code = c;
					text = t;
					done.Set();
				};

				Globals.RandomDispatcher.InvokeAsync(new Action(() =>
				{
					try
					{
						hds.RequestMarketReplay(instrument, dateEst, callback, new NoProgress(), null);
					}
					catch (Exception ex)
					{
						fail = ex.Message;
						done.Set();
					}
				}));

				if (!done.Wait(TimeSpan.FromMinutes(10)))
				{
					message = "timed out after 10 min";
					return false;
				}
			}

			if (fail != null)
			{
				message = "exception: " + fail;
				return false;
			}
			message = string.IsNullOrEmpty(text) ? code.ToString() : code + " " + text;
			return code == ErrorCode.NoError;
		}

		// One day, end to end: resolve the front-month contract, request it, and
		// confirm on disk. The file appearing is the real success signal — an
		// ErrorCode of NoError alone does not mean the server had the day.
		private bool FetchDay(HdsClient hds, string root, DateTime day, out string detail)
		{
			string contract = FrontMonth(day);
			string name     = root + " " + contract;

			Instrument instrument = Instrument.GetInstrument(name);
			if (instrument == null)
			{
				detail = name + ": not in the NT8 instrument database";
				return false;
			}

			string path   = ReplayPath(name, day);
			long   before = File.Exists(path) ? new FileInfo(path).Length : 0;

			string message;
			bool   ok = RequestDay(hds, instrument, day, out message);

			if (!File.Exists(path))
			{
				detail = name + ": no file (" + message + ")";
				return false;
			}
			long after = new FileInfo(path).Length;
			if (after == before && before > 0)
			{
				detail = name + ": already present, unchanged — " + DepthSummary(path);
				return ok;
			}
			detail = string.Format(CultureInfo.InvariantCulture, "{0}: {1:N0} MB — {2}",
				name, after / 1000000, DepthSummary(path));
			return true;
		}

		#endregion

		#region Jobs

		private void Start(bool probeOnly)
		{
			if (running)
				return;

			HdsClient hds = GetHdsClient();
			if (hds == null)
			{
				Log("ERROR: no HdsClient available. Connect to your data feed before running this.");
				return;
			}

			string[] roots = tbSymbols.Text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < roots.Length; i++)
				roots[i] = roots[i].Trim();

			DateTime to;
			if (!DateTime.TryParseExact(tbTo.Text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out to))
			{
				Log("ERROR: invalid 'To' date, use yyyy-MM-dd.");
				return;
			}

			DateTime from;
			bool hasFrom = DateTime.TryParseExact(tbFrom.Text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out from);

			int delay;
			if (!int.TryParse(tbDelay.Text.Trim(), out delay) || delay < 0)
				delay = 3;

			cancelling = false;
			SetRunning(true);

			Thread worker = new Thread(() =>
			{
				try
				{
					if (probeOnly)
						Probe(hds, roots.Length > 0 ? roots[0] : "NQ", to);
					else
						Download(hds, roots, hasFrom ? from : to.AddDays(-400), to, delay);
				}
				catch (Exception ex)
				{
					Log("EXCEPTION: " + ex);
				}
				finally
				{
					SetRunning(false);
					Log("-- finished --");
				}
			});
			worker.IsBackground = true;
			worker.Start();
		}

		// Binary search for the oldest session the server still serves. Successful
		// probes are real downloads, so they land as usable data rather than waste.
		private void Probe(HdsClient hds, string root, DateTime to)
		{
			Log("=== RETENTION PROBE (" + root + ") ===");
			List<DateTime> days = Sessions(to.AddDays(-400), to);
			if (days.Count == 0)
			{
				Log("no sessions in range.");
				return;
			}

			int lo = 0, hi = days.Count - 1;   // lo = oldest, hi = most recent
			int oldestOk = -1;
			string detail;

			Log("checking the recent end: " + days[hi].ToString("yyyy-MM-dd"));
			if (!FetchDay(hds, root, days[hi], out detail))
			{
				Log("  the most recent day already fails: " + detail);
				Log("  check the connection first — the probe cannot bracket anything.");
				return;
			}
			Log("  OK " + detail);
			oldestOk = hi;

			while (lo < hi && !cancelling)
			{
				int mid = lo + (hi - lo) / 2;
				Log("testing " + days[mid].ToString("yyyy-MM-dd") + "  (" + (hi - lo) + " sessions of uncertainty left)");
				if (FetchDay(hds, root, days[mid], out detail))
				{
					Log("  OK " + detail);
					oldestOk = mid;
					hi = mid;              // served: look further back
				}
				else if (!IsNoData(detail))
				{
					// A dropped connection would move the cut to an invented date.
					// Better to abort than to report a false floor.
					Log("  ABORTED: failure is not 'no data' -> " + detail);
					return;
				}
				else
				{
					Log("  NO " + detail);
					lo = mid + 1;          // not served: the cut is further forward
				}
				Thread.Sleep(1000);
			}

			if (oldestOk >= 0)
			{
				DateTime floor = days[oldestOk];
				Log("");
				Log(">>> OLDEST SESSION AVAILABLE: " + floor.ToString("yyyy-MM-dd"));
				Log(">>> retention ~" + (to.Date - floor.Date).Days + " calendar days");
				Log(">>> 'From' filled in with that date; you can hit Download missing now.");
				Dispatcher.InvokeAsync(new Action(() =>
					tbFrom.Text = floor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
			}
		}

		// Walks newest-first, so the days worth having arrive first and a run of
		// "no data" answers marks the retention floor.
		private void Download(HdsClient hds, string[] roots, DateTime from, DateTime to, int delay)
		{
			List<DateTime> days = Sessions(from, to);
			days.Reverse();
			Log(string.Format(CultureInfo.InvariantCulture,
				"=== DOWNLOAD: up to {0} sessions x {1} symbols, newest first, {2}s between requests ===",
				days.Count, roots.Length, delay));
			Log("    (stops only after the server reports no data " + NoDataStreakToStop + " times in a row)");

			int ok = 0, skip = 0, fail = 0;
			foreach (string root in roots)
			{
				int noDataStreak = 0;
				foreach (DateTime day in days)
				{
					if (cancelling)
					{
						Log("cancelled by user.");
						Log(string.Format(CultureInfo.InvariantCulture, "partial summary: {0} downloaded, {1} already present, {2} unavailable", ok, skip, fail));
						return;
					}

					string contract = FrontMonth(day);
					string path     = ReplayPath(root + " " + contract, day);

					// Already on disk WITH depth -> nothing to do. On disk without
					// depth -> re-request, that is exactly the case worth fixing.
					if (File.Exists(path) && DepthSummary(path) != NoDepth)
					{
						skip++;
						noDataStreak = 0;      // the day exists, we are not below the floor
						continue;
					}

					string detail;
					if (FetchDay(hds, root, day, out detail))
					{
						ok++;
						noDataStreak = 0;
						Log("OK  " + day.ToString("yyyy-MM-dd") + "  " + detail);
					}
					else
					{
						fail++;
						Log("--  " + day.ToString("yyyy-MM-dd") + "  " + detail);

						if (!IsNoData(detail))
						{
							// dropped connection, unknown instrument: a failure, not the floor
							noDataStreak = 0;
						}
						else if (++noDataStreak >= NoDataStreakToStop)
						{
							Log(">>> " + root + ": retention floor reached around "
								+ day.ToString("yyyy-MM-dd") + ", moving to the next symbol.");
							break;
						}
					}

					if (delay > 0)
						Thread.Sleep(delay * 1000);
				}
			}
			Log(string.Format(CultureInfo.InvariantCulture, "summary: {0} downloaded, {1} already present, {2} unavailable", ok, skip, fail));
		}

		#endregion

		#region IWorkspacePersistence

		public WorkspaceOptions WorkspaceOptions { get; set; }

		public void Restore(System.Xml.Linq.XDocument document, System.Xml.Linq.XElement element) { }
		public void Save(System.Xml.Linq.XDocument document, System.Xml.Linq.XElement element) { }

		#endregion
	}
}
