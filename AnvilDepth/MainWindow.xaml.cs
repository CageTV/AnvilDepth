using System;using System.IO;using System.Linq;using System.Runtime.InteropServices;using System.Security.Principal;using System.Text;using System.Threading;using System.Threading.Tasks;using System.Windows;using System.Windows.Input;using System.Windows.Interop;using System.Windows.Media;using System.Windows.Media.Imaging;using System.Windows.Threading;using Microsoft.Win32;using AnvilDepth.Services;
namespace AnvilDepth{
public partial class MainWindow:Window{
string? curPath;float[]? curDepth;byte[]? curBgra;int dW,dH;DepthEngine? eng;
// Cached raw AI depth output (before slider post-processing) so dragging sliders in AI mode
// re-runs only the fast CPU post-process (tone/detail/contrast), not the neural network.
// Cleared whenever a new image loads or the active model is swapped, since it's tied to both.
float[]? aiRawDepth;int aiRawW,aiRawH;
// AI background-removal mask (SegmentationEngine). Cached per-image (keyed by bgMaskPath) since
// it's expensive to compute but cheap to reapply, same reasoning as aiRawDepth above.
SegmentationEngine? seg;float[]? bgMask;string? bgMaskPath;
// Normal map — derived from curDepth (see ImageProcessor.ComputeNormalMap), not a separate AI
// model. Cached the same way as the other maps: generated on demand, reapplied cheaply on tweak.
byte[]? curNormalBgra;int normalW,normalH;
// Which map the big preview is currently showing ("depth" or "normal"); the thumbnail strip
// switches this without recomputing anything, since both bitmaps are already cached.
string activeMap="depth";
BitmapSource? depthBitmap;BitmapSource? normalBitmap;
static readonly Brush ActiveThumbBrush=new SolidColorBrush(Color.FromRgb(0x8B,0x5C,0xF6));
static readonly Brush InactiveThumbBrush=new SolidColorBrush(Color.FromRgb(0x33,0x33,0x33));
readonly DispatcherTimer liveTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(60)};
CancellationTokenSource? liveCts;
CancellationTokenSource? normalCts;
[DllImport("shell32.dll")]static extern void DragAcceptFiles(IntPtr h,bool f);
[DllImport("shell32.dll")]static extern uint DragQueryFile(IntPtr h,uint i,StringBuilder b,uint c);
[DllImport("shell32.dll")]static extern void DragFinish(IntPtr h);
const int WM_DROP=0x0233;
public MainWindow(){InitializeComponent();Loaded+=OnLoaded;SourceInitialized+=(s,e)=>{try{var hwnd=new WindowInteropHelper(this).Handle;var src=HwndSource.FromHwnd(hwnd);src?.AddHook(WndProc);DragAcceptFiles(hwnd,true);}catch{}};liveTimer.Tick+=(s,e)=>{liveTimer.Stop();DoLiveUpdate();};}
IntPtr WndProc(IntPtr h,int m,IntPtr w,IntPtr l,ref bool hd){if(m==WM_DROP){try{uint c=DragQueryFile(w,0xFFFFFFFF,null,0);if(c>0){var sb=new StringBuilder(1024);DragQueryFile(w,0,sb,(uint)sb.Capacity);Dispatcher.Invoke(()=>Load(sb.ToString()));}DragFinish(w);hd=true;}catch{}}return IntPtr.Zero;}
async void OnLoaded(object s,RoutedEventArgs e){try{bool admin=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);if(admin) DragDebugText.Text="Admin - drag blocked!";}catch{}GpuStatusText.Text="Ready V7.20 Checklist";eng=new DepthEngine();var st=await eng.InitializeAsync();GpuStatusText.Text=st;seg=new SegmentationEngine();await seg.InitializeAsync();AiModelCheck.IsChecked=false;UseLabCheck.IsChecked=true;PercentileCheck.IsChecked=true;}
bool DragOk(DragEventArgs e){if(e.Data.GetDataPresent(DataFormats.FileDrop)){var f=e.Data.GetData(DataFormats.FileDrop) as string[];if(f!=null&&f.Length>0){string ext=Path.GetExtension(f[0]).ToLower();if(new[]{".png",".jpg",".jpeg",".bmp",".tiff",".tga",".webp"}.Contains(ext)){e.Effects=DragDropEffects.Copy;e.Handled=true;DragDebugText.Text=$"Hover {Path.GetFileName(f[0])}";return true;}}}e.Effects=DragDropEffects.None;e.Handled=true;return false;}
void Window_DragEnter(object s,DragEventArgs e){DragOk(e);}void Window_DragOver(object s,DragEventArgs e){DragOk(e);}
void Window_Drop(object s,DragEventArgs e){try{if(e.Data.GetData(DataFormats.FileDrop) is string[] f&&f.Length>0) Load(f[0]);}catch(Exception ex){MessageBox.Show(ex.Message);}e.Handled=true;}
void DropZone_DragEnter(object s,DragEventArgs e){DragOk(e);}void DropZone_DragOver(object s,DragEventArgs e){DragOk(e);}
void DropZone_Drop(object s,DragEventArgs e){try{if(e.Data.GetData(DataFormats.FileDrop) is string[] f&&f.Length>0) Load(f[0]);}catch(Exception ex){MessageBox.Show(ex.Message);}e.Handled=true;}
void DropZone_MouseDown(object s,MouseButtonEventArgs e){var d=new OpenFileDialog{Filter="Images|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tga;*.webp"};if(d.ShowDialog()==true) Load(d.FileName);}
async void Load(string path){try{
curPath=path;aiRawDepth=null;bgMask=null;bgMaskPath=null;
ClearNormalMap();
var bmp=new BitmapImage(new Uri(path));InputPreview.Source=bmp;InputPreview.Visibility=Visibility.Visible;DropText.Visibility=Visibility.Collapsed;var wb=new WriteableBitmap(bmp);wb=new WriteableBitmap(new FormatConvertedBitmap(wb,PixelFormats.Bgra32,null,0));int st=wb.PixelWidth*4;byte[] pix=new byte[wb.PixelHeight*st];wb.CopyPixels(pix,st,0);curBgra=pix;dW=wb.PixelWidth;dH=wb.PixelHeight;GenerateBtn.IsEnabled=true;GpuStatusText.Text=$"{Path.GetFileName(path)} {dW}x{dH}";
if(AiModelCheck.IsChecked==false) Reproc();
if(RemoveBgCheck.IsChecked==true){await EnsureBgMaskAsync();ScheduleLiveUpdate();}
}catch(Exception ex){MessageBox.Show(ex.Message);}}
// Computes (and caches) the AI background mask for the current image. No-op if no bg_remove.onnx
// is loaded — callers fall back to alpha-channel removal automatically inside ImageProcessor when
// bgMask stays null, so this failing quietly is the correct behavior, not a bug.
async Task EnsureBgMaskAsync(){
if(seg==null||!seg.IsLoaded) return;
if(curPath==null) return;
if(bgMask!=null&&bgMaskPath==curPath) return;
string path=curPath;
try{
var result=await seg.ComputeMaskAsync(path);
if(curPath==path){bgMask=result.Mask;bgMaskPath=path;}
}catch(Exception ex){
MessageBox.Show($"AI background removal failed, falling back to alpha-channel removal: {ex.Message}");
}
}
async void RemoveBg_Changed(object s,RoutedEventArgs e){
if(RemoveBgCheck.IsChecked==true) await EnsureBgMaskAsync();
ScheduleLiveUpdate();
}
async void Generate_Click(object s,RoutedEventArgs e){
float fl=(float)FlattenSlider.Value,flR=(float)FlattenRadiusSlider.Value,lowF=(float)LowFreqSlider.Value,midF=(float)MidFreqSlider.Value,highF=(float)HighFreqSlider.Value,det=(float)DetailSlider.Value,gam=(float)GammaSlider.Value,str=(float)StrengthSlider.Value,hi=(float)HighlightsSlider.Value,mid=(float)MidtonesSlider.Value,sh=(float)ShadowsSlider.Value;bool inv=InvertCheck.IsChecked==true,rem=RemoveBgCheck.IsChecked==true,hq=HighQualityCheck.IsChecked==true,ai=AiModelCheck.IsChecked==true,lab=UseLabCheck.IsChecked==true,seam=SeamlessCheck.IsChecked==true;float seamB=(float)SeamBlendSlider.Value;bool zeroMid=ZeroMidGrayCheck.IsChecked==true;float zeroL=(float)ZeroLevelSlider.Value+(zeroMid?0.5f:0f);bool perc=PercentileCheck.IsChecked==true;
if(rem) await EnsureBgMaskAsync();
var bgra=curBgra;int w=dW,h=dH;string? p=curPath;var mask=bgMask;
try{
if(p==null) return;
if(!ai){if(bgra==null) return;GenerateBtn.Content="PROCESSING...";GenerateBtn.IsEnabled=false;float[] proc=null!;await Task.Run(()=>{proc=ImageProcessor.ProcessTextureAtlasAdvanced(bgra!,w,h,det,gam,inv,hi,mid,sh,rem,lab,fl,flR,lowF,midF,highF,seam,seamB,zeroMid,zeroL,perc,0.02f,0.98f,mask);});curDepth=proc;SetDepthBitmap(ImageProcessor.FloatArrayToBitmapSource(proc,w,h));GenerateBtn.Content="DONE";GenerateBtn.IsEnabled=true;SavePngBtn.IsEnabled=true;SaveExrBtn.IsEnabled=true;SaveTiffBtn.IsEnabled=true;SaveStlBtn.IsEnabled=true;GenerateNormalBtn.IsEnabled=true;SaveAllBtn.IsEnabled=true;return;}
GenerateBtn.Content="GENERATING...";GenerateBtn.IsEnabled=false;
var res=await eng!.EstimateDepthAsync(p!,hq);
aiRawDepth=res.Depth;aiRawW=res.Width;aiRawH=res.Height;
var proc2=ImageProcessor.ProcessForSculptOKQuality(res.Depth,res.Width,res.Height,bgra,str,det,lowF,midF,highF,gam,inv,hi,mid,sh,zeroMid,zeroL,rem,mask);
curDepth=proc2;dW=res.Width;dH=res.Height;SetDepthBitmap(ImageProcessor.FloatArrayToBitmapSource(proc2,dW,dH));
GenerateBtn.Content="DONE";GenerateBtn.IsEnabled=true;SavePngBtn.IsEnabled=true;SaveExrBtn.IsEnabled=true;SaveTiffBtn.IsEnabled=true;SaveStlBtn.IsEnabled=true;GenerateNormalBtn.IsEnabled=true;SaveAllBtn.IsEnabled=true;
}catch(Exception ex){MessageBox.Show(ex.Message);GenerateBtn.Content="FAILED";GenerateBtn.IsEnabled=true;}}
void Mode_Changed(object s,RoutedEventArgs? e){if(AiModelCheck==null) return;GenerateBtn.Content=AiModelCheck.IsChecked==true?"AI DEPTH":"RELIEF";}
// Swaps the loaded ONNX model (Small/Base/Large) at runtime. Only replaces the active session
// on success — see DepthEngine.LoadModelAsync — so picking a size whose file isn't downloaded
// yet just shows a status message and keeps whatever model was working before.
async void ModelSize_Changed(object s,RoutedEventArgs e){
if(eng==null) return;
string file=ModelBaseRadio.IsChecked==true?"model_base.onnx":ModelLargeRadio.IsChecked==true?"model_large.onnx":"model.onnx";
bool wasEnabled=GenerateBtn.IsEnabled;GenerateBtn.IsEnabled=false;
GpuStatusText.Text=$"Loading {file}...";
var status=await eng.LoadModelAsync(file);
GpuStatusText.Text=status;
GenerateBtn.IsEnabled=wasEnabled;
aiRawDepth=null;
ClearNormalMap(); // dimensions may change with a different model — stale normal map would mismatch
// If we already have an image loaded and are in AI mode, regenerate immediately with the newly
// selected model so the preview reflects the switch instead of showing stale depth.
if(curPath!=null&&AiModelCheck.IsChecked==true) Generate_Click(this,new RoutedEventArgs());
}
void Reproc(){try{if(AiModelCheck.IsChecked==true) return;if(curBgra==null) return;float fl=(float)FlattenSlider.Value,flR=(float)FlattenRadiusSlider.Value,lowF=(float)LowFreqSlider.Value,midF=(float)MidFreqSlider.Value,highF=(float)HighFreqSlider.Value,det=(float)DetailSlider.Value,gam=(float)GammaSlider.Value,hi=(float)HighlightsSlider.Value,mid=(float)MidtonesSlider.Value,sh=(float)ShadowsSlider.Value;bool inv=InvertCheck.IsChecked==true,rem=RemoveBgCheck.IsChecked==true,lab=UseLabCheck.IsChecked==true,seam=SeamlessCheck.IsChecked==true;float seamB=(float)SeamBlendSlider.Value;bool zeroMid=ZeroMidGrayCheck.IsChecked==true;float zeroL=(float)ZeroLevelSlider.Value+(zeroMid?0.5f:0f);bool perc=PercentileCheck.IsChecked==true;var proc=ImageProcessor.ProcessTextureAtlasAdvanced(curBgra!,dW,dH,det,gam,inv,hi,mid,sh,rem,lab,fl,flR,lowF,midF,highF,seam,seamB,zeroMid,zeroL,perc,0.02f,0.98f,bgMask);curDepth=proc;SetDepthBitmap(ImageProcessor.FloatArrayToBitmapSource(proc,dW,dH));GenerateNormalBtn.IsEnabled=true;SaveAllBtn.IsEnabled=true;}catch{}}
// AI-mode counterpart to Reproc(): re-runs only the post-process (contrast/detail/tone/bg-mask) on
// the already-computed aiRawDepth, off the UI thread, so slider drags stay smooth instead of
// re-running the neural network on every tick. Uses a "latest wins" cancellation token so a
// fast drag doesn't queue up stale renders behind the current one.
void ReprocAiLive(){
if(aiRawDepth==null) return;
float str=(float)StrengthSlider.Value,det=(float)DetailSlider.Value,lowF=(float)LowFreqSlider.Value,midF=(float)MidFreqSlider.Value,highF=(float)HighFreqSlider.Value,gam=(float)GammaSlider.Value,hi=(float)HighlightsSlider.Value,mid=(float)MidtonesSlider.Value,sh=(float)ShadowsSlider.Value;
bool inv=InvertCheck.IsChecked==true;bool rem=RemoveBgCheck.IsChecked==true;bool zeroMid=ZeroMidGrayCheck.IsChecked==true;float zeroL=(float)ZeroLevelSlider.Value+(zeroMid?0.5f:0f);
var depth=aiRawDepth;int w=aiRawW,h=aiRawH;var bgra=curBgra;var mask=bgMask;
liveCts?.Cancel();var cts=new CancellationTokenSource();liveCts=cts;
Task.Run(()=>{
if(cts.IsCancellationRequested) return;
var proc=ImageProcessor.ProcessForSculptOKQuality(depth!,w,h,bgra,str,det,lowF,midF,highF,gam,inv,hi,mid,sh,zeroMid,zeroL,rem,mask);
if(cts.IsCancellationRequested) return;
Dispatcher.Invoke(()=>{
if(cts.IsCancellationRequested) return;
curDepth=proc;dW=w;dH=h;SetDepthBitmap(ImageProcessor.FloatArrayToBitmapSource(proc,w,h));
SavePngBtn.IsEnabled=true;SaveExrBtn.IsEnabled=true;SaveTiffBtn.IsEnabled=true;SaveStlBtn.IsEnabled=true;GenerateNormalBtn.IsEnabled=true;SaveAllBtn.IsEnabled=true;
});
});
}
// Debounced entry point for every slider drag / toggle flip: restarts a short timer so a fast
// drag collapses into one update instead of one Task.Run per pixel of mouse movement.
void ScheduleLiveUpdate(){liveTimer.Stop();liveTimer.Start();}
void DoLiveUpdate(){if(AiModelCheck.IsChecked==true) ReprocAiLive();else Reproc();}
void Toggle_Changed(object s,RoutedEventArgs e){ScheduleLiveUpdate();}
void Slider_ValueChanged(object s,RoutedPropertyChangedEventArgs<double> e){if(StrengthLabel!=null) StrengthLabel.Text=StrengthSlider.Value.ToString("0.0");if(DetailLabel!=null) DetailLabel.Text=DetailSlider.Value.ToString("0.00");if(GammaLabel!=null) GammaLabel.Text=GammaSlider.Value.ToString("0.00");if(ShadowsLabel!=null) ShadowsLabel.Text=ShadowsSlider.Value.ToString("0.00");if(MidtonesLabel!=null) MidtonesLabel.Text=MidtonesSlider.Value.ToString("0.00");if(HighlightsLabel!=null) HighlightsLabel.Text=HighlightsSlider.Value.ToString("0.00");if(FlattenLabel!=null) FlattenLabel.Text=FlattenSlider.Value.ToString("0.00");if(FlattenRadiusLabel!=null) FlattenRadiusLabel.Text=FlattenRadiusSlider.Value.ToString("0");if(LowFreqLabel!=null) LowFreqLabel.Text=LowFreqSlider.Value.ToString("0.00");if(MidFreqLabel!=null) MidFreqLabel.Text=MidFreqSlider.Value.ToString("0.00");if(HighFreqLabel!=null) HighFreqLabel.Text=HighFreqSlider.Value.ToString("0.00");if(SeamBlendLabel!=null) SeamBlendLabel.Text=SeamBlendSlider.Value.ToString("0.00");if(ZeroLevelLabel!=null) ZeroLevelLabel.Text=ZeroLevelSlider.Value.ToString("0.00");if(AiModelCheck!=null&&((AiModelCheck.IsChecked==false&&curBgra!=null)||(AiModelCheck.IsChecked==true&&aiRawDepth!=null))) ScheduleLiveUpdate();}
// --- Normal map ---------------------------------------------------------------------------
void SetDepthBitmap(System.Windows.Media.Imaging.BitmapSource bmp){depthBitmap=bmp;DepthThumb.Source=bmp;if(activeMap=="depth") OutputImage.Source=bmp;}
void SetNormalBitmap(System.Windows.Media.Imaging.BitmapSource bmp){normalBitmap=bmp;NormalThumb.Source=bmp;NormalThumbBorder.Visibility=Visibility.Visible;if(activeMap=="normal") OutputImage.Source=bmp;}
void ClearNormalMap(){curNormalBgra=null;normalBitmap=null;NormalThumbBorder.Visibility=Visibility.Collapsed;NormalThumb.Source=null;SaveNormalBtn.IsEnabled=false;if(activeMap=="normal"){activeMap="depth";if(depthBitmap!=null) OutputImage.Source=depthBitmap;}HighlightThumbs();}
void HighlightThumbs(){DepthThumbBorder.BorderBrush=activeMap=="depth"?ActiveThumbBrush:InactiveThumbBrush;NormalThumbBorder.BorderBrush=activeMap=="normal"?ActiveThumbBrush:InactiveThumbBrush;}
void DepthThumb_Click(object s,MouseButtonEventArgs e){activeMap="depth";if(depthBitmap!=null) OutputImage.Source=depthBitmap;HighlightThumbs();}
void NormalThumb_Click(object s,MouseButtonEventArgs e){if(normalBitmap==null) return;activeMap="normal";OutputImage.Source=normalBitmap;HighlightThumbs();}
void GenerateNormal_Click(object s,RoutedEventArgs e){RecomputeNormalMap();}
void NormalStrengthSlider_ValueChanged(object s,RoutedPropertyChangedEventArgs<double> e){if(NormalStrengthLabel!=null) NormalStrengthLabel.Text=NormalStrengthSlider.Value.ToString("0.0");if(curNormalBgra!=null) RecomputeNormalMap();}
void InvertNormalY_Changed(object s,RoutedEventArgs e){if(curNormalBgra!=null) RecomputeNormalMap();}
// Recomputes the normal map from the currently-displayed depth map (curDepth) off the UI thread.
// Cheap (a Sobel pass, not a network), but still backgrounded + cancellable so dragging Strength
// stays smooth on large textures, same "latest wins" pattern as ReprocAiLive.
void RecomputeNormalMap(){
if(curDepth==null) return;
float strength=(float)NormalStrengthSlider.Value;bool invY=InvertNormalYCheck.IsChecked==true;
var depth=curDepth;int w=dW,h=dH;
normalCts?.Cancel();var cts=new CancellationTokenSource();normalCts=cts;
Task.Run(()=>{
if(cts.IsCancellationRequested) return;
var bgra=ImageProcessor.ComputeNormalMap(depth!,w,h,strength,invY);
if(cts.IsCancellationRequested) return;
Dispatcher.Invoke(()=>{
if(cts.IsCancellationRequested) return;
curNormalBgra=bgra;normalW=w;normalH=h;
SetNormalBitmap(ImageProcessor.BgraArrayToBitmapSource(bgra,w,h));
SaveNormalBtn.IsEnabled=true;SaveAllBtn.IsEnabled=true;
});
});
}
void SaveNormal_Click(object s,RoutedEventArgs e){if(curNormalBgra==null) return;var d=new SaveFileDialog{Filter="PNG|*.png",FileName="normal_map.png"};if(d.ShowDialog()==true) ImageProcessor.SaveNormalMapPng(curNormalBgra,normalW,normalH,d.FileName);}
// Saves every currently-generated map into one chosen folder — depth (16-bit PNG + 32-bit EXR +
// STL) and the normal map (PNG) if one's been generated — without touching the individual Save
// buttons above, which still work exactly as before if you only want one specific file.
void SaveAll_Click(object s,RoutedEventArgs e){
if(curDepth==null) return;
using var fbd=new System.Windows.Forms.FolderBrowserDialog{Description="Choose a folder to save all maps into"};
if(fbd.ShowDialog()!=System.Windows.Forms.DialogResult.OK) return;
string dir=fbd.SelectedPath;
string baseName=curPath!=null?Path.GetFileNameWithoutExtension(curPath):"output";
try{
ImageProcessor.SaveAs16Bit(curDepth,dW,dH,Path.Combine(dir,$"{baseName}_depth_16bit.png"));
ImageProcessor.SaveAsEXR(curDepth,dW,dH,Path.Combine(dir,$"{baseName}_depth_32bit.exr"));
StlExporter.SaveAsStl(curDepth,dW,dH,Path.Combine(dir,$"{baseName}_relief.stl"),10f);
if(curNormalBgra!=null) ImageProcessor.SaveNormalMapPng(curNormalBgra,normalW,normalH,Path.Combine(dir,$"{baseName}_normal.png"));
MessageBox.Show($"Saved to {dir}");
}catch(Exception ex){MessageBox.Show($"Save All failed: {ex.Message}");}
}
// -------------------------------------------------------------------------------------------
void SavePng_Click(object s,RoutedEventArgs e){if(curDepth==null) return;var d=new SaveFileDialog{Filter="PNG 16-bit|*.png",FileName="depth_16bit.png"};if(d.ShowDialog()==true) ImageProcessor.SaveAs16Bit(curDepth,dW,dH,d.FileName);}
void SaveExr_Click(object s,RoutedEventArgs e){if(curDepth==null) return;var d=new SaveFileDialog{Filter="EXR|*.exr",FileName="depth_32bit.exr"};if(d.ShowDialog()==true) ImageProcessor.SaveAsEXR(curDepth,dW,dH,d.FileName);}
void SaveTiffBtn_Click(object s,RoutedEventArgs e){if(curDepth==null) return;var d=new SaveFileDialog{Filter="TIFF|*.tiff",FileName="depth_32bit.tiff"};if(d.ShowDialog()==true) ImageProcessor.SaveAsTiff32(curDepth,dW,dH,d.FileName);}
void SaveStl_Click(object s,RoutedEventArgs e){if(curDepth==null) return;var d=new SaveFileDialog{Filter="STL|*.stl",FileName="relief.stl"};if(d.ShowDialog()==true) StlExporter.SaveAsStl(curDepth,dW,dH,d.FileName,10f);}
}
}
