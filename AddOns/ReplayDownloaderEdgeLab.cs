// EdgeLab hardened derivative of jalv92/NT8ReplayDownloader (MIT).
// Upstream commit d4a9c7aa2048846ab6e6aa45b8f23530c5d60af3.
// Exact-contract, fail-closed acquisition. Verified assumptions: NT 8.1.8.2 (reflection API surface + .nrd header layout re-verified 2026-09-22; also compiles against 8.1.8.0 but that was not re-tested).
#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
 public class EdgeLabReplayDownloaderAddOn : AddOnBase
 {
  NTMenuItem item, tools;
  protected override void OnStateChange(){if(State==State.SetDefaults){Name="EdgeLab Replay Downloader";Description="Fail-closed exact-contract Market Replay acquisition.";}}
  protected override void OnWindowCreated(Window w){ControlCenter c=w as ControlCenter;if(c==null)return;tools=c.FindFirst("ControlCenterMenuItemTools") as NTMenuItem;if(tools==null)return;item=new NTMenuItem{Header="EdgeLab Replay Downloader",Style=Application.Current.TryFindResource("MainMenuItem") as Style};tools.Items.Add(item);item.Click+=Open;}
  protected override void OnWindowDestroyed(Window w){if(item==null||!(w is ControlCenter))return;item.Click-=Open;if(tools!=null&&tools.Items.Contains(item))tools.Items.Remove(item);item=null;tools=null;}
  void Open(object s,RoutedEventArgs e){Globals.RandomDispatcher.BeginInvoke(new Action(()=>new EdgeLabReplayDownloaderWindow().Show()));}
 }
 internal sealed class EdgeProgress:NinjaTrader.Core.IProgress
 {
  volatile bool aborted; public bool IsAborted{get{return aborted;}} public string Message{get;set;} public event EventHandler Aborted;
  public void Abort(){aborted=true;EventHandler h=Aborted;if(h!=null)h(this,EventArgs.Empty);} public void PerformStep(){} public void SetUp(long m,bool a){} public void TearDown(){}
 }
 internal enum DepthState{Pass,Missing,Truncated,Unreadable,Invalid,NoDepth,Unstable}
 internal sealed class DepthCheck
 {
  public DepthState State; public long Ask,Bid,Volume,Bytes,T0,T1; public string Detail; public bool Pass{get{return State==DepthState.Pass;}}
 }
 internal enum RequestState{Success,Error,Timeout,Cancelled,DispatchError}
 internal sealed class RequestResult{public RequestState State;public ErrorCode Code;public string Message;}
 public class EdgeLabReplayDownloaderWindow:NTWindow,IWorkspacePersistence
 {
  const string Build="EDGE_NT8RD_HARDENED_V1",Upstream="d4a9c7aa2048846ab6e6aa45b8f23530c5d60af3",Verified="8.1.8.2",NoData="no market replay data";
  const int HeaderBytes=44*80,TimeoutMinutes=10;
  static readonly Regex ContractRx=new Regex(@"^[A-Za-z0-9]{1,8}\s+[0-9]{2}-[0-9]{2}$",RegexOptions.Compiled);
  TextBox contracts,from,to,delay,log;Button go,stop;volatile bool running,cancel;EdgeProgress active;
  public EdgeLabReplayDownloaderWindow(){Caption="EdgeLab Replay Downloader";Width=720;Height=600;Content=Ui();Loaded+=(o,e)=>{if(WorkspaceOptions==null)WorkspaceOptions=new WorkspaceOptions("EdgeLabReplayDownloader-"+Guid.NewGuid().ToString("N"),this);};}
  protected override void OnClosed(EventArgs e){Stop();base.OnClosed(e);}
  DependencyObject Ui(){double m=(double)FindResource("MarginBase");Grid g=new Grid{Margin=new Thickness(m)};g.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});g.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});g.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});StackPanel f=new StackPanel();contracts=Field(f,m,"Exact contracts; no inferred rolls (NQ 09-26;GC 08-26):","");from=Field(f,m,"From inclusive (yyyy-MM-dd):","");to=Field(f,m,"To inclusive, before today (yyyy-MM-dd):",DateTime.Now.Date.AddDays(-1).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));delay=Field(f,m,"Delay seconds (3-60):","3");Grid.SetRow(f,0);g.Children.Add(f);StackPanel b=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(m)};go=new Button{Content="_Acquire exact range",Padding=new Thickness(m,2,m,2),Margin=new Thickness(0,0,m,0)};stop=new Button{Content="_Stop",Padding=new Thickness(m,2,m,2),IsEnabled=false};go.Click+=(o,e)=>Start();stop.Click+=(o,e)=>Stop();b.Children.Add(go);b.Children.Add(stop);Grid.SetRow(b,1);g.Children.Add(b);log=new TextBox{Margin=new Thickness(m),IsReadOnly=true,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new FontFamily("Consolas")};Grid.SetRow(log,2);g.Children.Add(log);return g;}
  TextBox Field(Panel p,double m,string label,string value){p.Children.Add(new Label{Content=label,Foreground=FindResource("FontLabelBrush") as Brush,Margin=new Thickness(m,m,m,0)});TextBox t=new TextBox{Text=value,Margin=new Thickness(m,0,m,0)};p.Children.Add(t);return t;}
  void Log(string x){Dispatcher.InvokeAsync(new Action(()=>{if(log==null)return;log.AppendText(DateTime.Now.ToString("HH:mm:ss",CultureInfo.InvariantCulture)+"  "+x+Environment.NewLine);log.ScrollToEnd();}));}
  void Running(bool x){running=x;Dispatcher.InvokeAsync(new Action(()=>{go.IsEnabled=!x;stop.IsEnabled=x;}));}
  void Stop(){cancel=true;EdgeProgress p=active;if(p!=null)p.Abort();if(running)Log("-- stop requested; no further request will start --");}
  static string PathFor(string c,DateTime d){return Path.Combine(Globals.UserDataDir,"db","replay",c,d.ToString("yyyyMMdd",CultureInfo.InvariantCulture)+".nrd");}
  static DepthCheck Check(string path)
  {
   DepthCheck r=new DepthCheck{State=DepthState.Missing,Detail="missing"};if(!File.Exists(path))return r;if(!BitConverter.IsLittleEndian){r.State=DepthState.Unreadable;r.Detail="non-little-endian runtime";return r;}
   try{FileInfo fi=new FileInfo(path);r.Bytes=fi.Length;if(fi.Length<HeaderBytes){r.State=DepthState.Truncated;r.Detail="short header";return r;}byte[] h=new byte[HeaderBytes];using(FileStream fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)){int n=0;while(n<h.Length){int q=fs.Read(h,n,h.Length-n);if(q<=0)break;n+=q;}if(n<h.Length){r.State=DepthState.Truncated;r.Detail="short read";return r;}}
    long first=long.MaxValue,last=long.MinValue;for(int slot=10;slot<=11;slot++){int count=BitConverter.ToInt32(h,slot*80+8);long t0=BitConverter.ToInt64(h,slot*80+56),t1=BitConverter.ToInt64(h,slot*80+64),v=BitConverter.ToInt64(h,slot*80+72);if(count<0||v<0||(count==0&&v!=0)||(count>0&&(t0<=0||t1<=0||t0>t1))){r.State=DepthState.Invalid;r.Detail="invalid counts/times";return r;}if(slot==10)r.Ask=count;else r.Bid=count;r.Volume+=v;if(count>0){if(t0<first)first=t0;if(t1>last)last=t1;}}
    if(r.Ask+r.Bid==0){r.State=DepthState.NoDepth;r.Detail="zero L2";return r;}if(r.Ask==0||r.Bid==0){r.State=DepthState.Invalid;r.Detail="one-sided L2";return r;}r.T0=first;r.T1=last;if(first>last){r.State=DepthState.Invalid;r.Detail="inverted time range";return r;}r.State=DepthState.Pass;r.Detail=string.Format(CultureInfo.InvariantCulture,"PASS ask={0:N0} bid={1:N0} mean={2:F2}",r.Ask,r.Bid,(double)r.Volume/(r.Ask+r.Bid));return r;
   }catch(Exception ex){r.State=DepthState.Unreadable;r.Detail="unreadable:"+ex.GetType().Name;return r;}
  }
  DepthCheck Stable(string path,TimeSpan timeout){DateTime end=DateTime.UtcNow+timeout;long last=-1;int same=0;DepthCheck r=new DepthCheck{State=DepthState.Missing,Detail="missing"};while(DateTime.UtcNow<end){if(cancel){r.State=DepthState.Unstable;r.Detail="cancelled";return r;}if(File.Exists(path)){long n;try{n=new FileInfo(path).Length;}catch{n=-1;}if(n>0&&n==last)same++;else same=0;last=n;if(same>=6){r=Check(path);return r;}}Thread.Sleep(500);}r=Check(path);if(r.Pass){r.State=DepthState.Unstable;r.Detail="file never reached stable gate";}return r;}
  static bool Hash(string path,out string value,out string detail){try{using(SHA256 h=SHA256.Create())using(FileStream f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){byte[] b=h.ComputeHash(f);StringBuilder s=new StringBuilder();foreach(byte x in b)s.Append(x.ToString("x2",CultureInfo.InvariantCulture));value=s.ToString();detail="";return true;}}catch(Exception ex){value="";detail="sha256:"+ex.GetType().Name;return false;}}
  static string Quarantine(string path){string q=path+".invalid."+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ",CultureInfo.InvariantCulture);File.Move(path,q);return q;}
  static HdsClient Client(out string detail){PropertyInfo p=typeof(Connection).GetProperty("HistoricalDataClient",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);if(p==null){detail="property absent";return null;}HashSet<HdsClient> set=new HashSet<HdsClient>();try{Connection c=Connection.ClientConnection;if(c!=null){HdsClient h=p.GetValue(c,null) as HdsClient;if(h!=null)set.Add(h);}foreach(Connection x in Connection.Connections){HdsClient h=p.GetValue(x,null) as HdsClient;if(h!=null)set.Add(h);}}catch(Exception ex){detail=ex.GetType().Name;return null;}if(set.Count!=1){detail="expected one HdsClient; found "+set.Count;return null;}foreach(HdsClient h in set){detail="one client";return h;}detail="none";return null;}
  RequestResult Request(HdsClient h,Instrument i,DateTime d)
  {
   TaskCompletionSource<RequestResult> t=new TaskCompletionSource<RequestResult>();EdgeProgress p=new EdgeProgress();active=p;Action<ErrorCode,string,object> cb=(c,m,s)=>t.TrySetResult(new RequestResult{State=c==ErrorCode.NoError?RequestState.Success:RequestState.Error,Code=c,Message=m});
   try{Globals.RandomDispatcher.InvokeAsync(new Action(()=>{try{h.RequestMarketReplay(i,d,cb,p,null);}catch(Exception ex){t.TrySetResult(new RequestResult{State=RequestState.DispatchError,Message=ex.GetType().Name});}}));DateTime end=DateTime.UtcNow.AddMinutes(TimeoutMinutes);while(!t.Task.Wait(TimeSpan.FromMilliseconds(250))){if(cancel){p.Abort();return new RequestResult{State=RequestState.Cancelled,Message="cancelled"};}if(DateTime.UtcNow>=end){p.Abort();return new RequestResult{State=RequestState.Timeout,Message="timeout"};}}return t.Task.Result;}finally{active=null;}
  }
  static bool IsNoData(RequestResult r){return r!=null&&r.Message!=null&&r.Message.IndexOf(NoData,StringComparison.OrdinalIgnoreCase)>=0;}
  static string Version(){System.Version v=typeof(Connection).Assembly.GetName().Version;return v==null?"unknown":v.ToString();}
  static string C(string s){s=s??"";return "\""+s.Replace("\"","\"\"").Replace("\r"," ").Replace("\n"," ")+"\"";}
  static void Manifest(string contract,DateTime day,string status,string path,DepthCheck d,string sha,string req,string detail)
  {string m=Path.Combine(Globals.UserDataDir,"db","replay","edgelab_replay_manifest.csv");bool head=!File.Exists(m);using(FileStream f=new FileStream(m,FileMode.Append,FileAccess.Write,FileShare.Read))using(StreamWriter w=new StreamWriter(f,new UTF8Encoding(false))){if(head)w.WriteLine("observed_at_utc,build,upstream,nt_core,contract,date,status,request,path,bytes,sha256,ask_events,bid_events,volume,first_raw_ts,last_raw_ts,continuity,detail");w.WriteLine(string.Join(",",new[]{C(DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)),C(Build),C(Upstream),C(Version()),C(contract),C(day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)),C(status),C(req),C(path),C(d==null?"0":d.Bytes.ToString(CultureInfo.InvariantCulture)),C(sha),C(d==null?"0":d.Ask.ToString(CultureInfo.InvariantCulture)),C(d==null?"0":d.Bid.ToString(CultureInfo.InvariantCulture)),C(d==null?"0":d.Volume.ToString(CultureInfo.InvariantCulture)),C(d==null?"0":d.T0.ToString(CultureInfo.InvariantCulture)),C(d==null?"0":d.T1.ToString(CultureInfo.InvariantCulture)),C("UNVALIDATED_REQUIRES_EDGELAB_BOUNDARY_CHECK"),C(detail)}));}}
  void Start()
  {if(running)return;if(Version()!=Verified){Log("ABSTAIN: unsupported NT Core "+Version()+"; verified "+Verified);return;}string cd;HdsClient h=Client(out cd);if(h==null){Log("ABSTAIN: "+cd);return;}HashSet<string> set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(string x in contracts.Text.Split(new[]{';'},StringSplitOptions.RemoveEmptyEntries)){string c=x.Trim().ToUpperInvariant();if(!ContractRx.IsMatch(c)){Log("ERROR exact contract required: "+c);return;}set.Add(c);}if(set.Count<1||set.Count>20){Log("ERROR: 1-20 contracts required");return;}DateTime a,b;if(!DateTime.TryParseExact(from.Text.Trim(),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out a)||!DateTime.TryParseExact(to.Text.Trim(),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out b)||a>b||b.Date>=DateTime.Now.Date||(b-a).TotalDays+1>120){Log("ERROR: valid past range <=120 days required");return;}int wait;if(!int.TryParse(delay.Text.Trim(),out wait)||wait<3||wait>60){Log("ERROR delay 3-60");return;}cancel=false;Running(true);Thread t=new Thread(()=>Run(h,new List<string>(set),a.Date,b.Date,wait));t.IsBackground=true;t.Start();}
  void Run(HdsClient h,List<string> list,DateTime a,DateTime b,int wait)
  {int ok=0,skip=0,none=0,fail=0;try{foreach(string contract in list){Instrument i=Instrument.GetInstrument(contract,true);if(i==null){Manifest(contract,a,"ABSTAIN_INSTRUMENT_NOT_FOUND","",null,"","NOT_REQUESTED","instrument absent");fail++;continue;}for(DateTime d=a;d<=b;d=d.AddDays(1)){if(cancel)return;string path=PathFor(contract,d);DepthCheck before=Check(path);if(before.Pass){string sha,hd;if(!Hash(path,out sha,out hd)){Manifest(contract,d,"ABSTAIN_HASH_FAILED",path,before,"","NOT_REQUESTED",hd);fail++;continue;}Manifest(contract,d,"SKIP_EXISTING_VALID",path,before,sha,"NOT_REQUESTED",before.Detail);skip++;continue;}if(File.Exists(path)){try{Log("QUARANTINE "+Quarantine(path));}catch(Exception ex){Manifest(contract,d,"ABSTAIN_QUARANTINE_FAILED",path,before,"","NOT_REQUESTED",ex.GetType().Name);fail++;continue;}}RequestResult rr=Request(h,i,d);if(rr.State==RequestState.Timeout||rr.State==RequestState.Cancelled){Manifest(contract,d,"ABSTAIN_REQUEST_TERMINAL",path,null,"",rr.State.ToString(),rr.Message);Log("TERMINAL "+rr.State+"; batch stopped");return;}if(IsNoData(rr)&&!File.Exists(path)){Manifest(contract,d,"NO_SERVER_DATA",path,null,"",rr.State.ToString(),"server no data");none++;}else{DepthCheck after=Stable(path,TimeSpan.FromSeconds(30));if(after.Pass){string sha,hd;if(Hash(path,out sha,out hd)){Manifest(contract,d,"ACQUIRED_VALID_DEPTH",path,after,sha,rr.State.ToString(),after.Detail);Log("PASS "+d.ToString("yyyy-MM-dd")+" "+contract+" "+after.Detail);ok++;}else{Manifest(contract,d,"ABSTAIN_HASH_FAILED",path,after,"",rr.State.ToString(),hd);fail++;}}else{string sha="",hd;if(File.Exists(path))Hash(path,out sha,out hd);Manifest(contract,d,"ABSTAIN_INVALID_DOWNLOAD",path,after,sha,rr.State.ToString(),after.Detail);fail++;}}if(d<b&&!cancel)Thread.Sleep(wait*1000);}}}catch(Exception ex){Log("ABSTAIN UNHANDLED "+ex.GetType().Name);}finally{Running(false);Log(string.Format(CultureInfo.InvariantCulture,"-- finished {0} acquired, {1} existing, {2} no-data, {3} failed --",ok,skip,none,fail));}}
  public WorkspaceOptions WorkspaceOptions{get;set;}public void Restore(System.Xml.Linq.XDocument d,System.Xml.Linq.XElement e){}public void Save(System.Xml.Linq.XDocument d,System.Xml.Linq.XElement e){}
 }
}
