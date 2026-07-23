using System;using System.Collections.Generic;using System.Diagnostics;using System.IO;using System.Linq;using System.Runtime.InteropServices;using System.Security.Principal;using System.Text;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using System.Windows;using System.Windows.Controls;using System.Windows.Input;using System.Windows.Interop;using System.Windows.Media;using System.Windows.Media.Imaging;using System.Windows.Media.Media3D;using System.Windows.Threading;using Microsoft.Win32;using AnvilDepth.Services;
namespace AnvilDepth{
public partial class MainWindow:Window{
string? curPath;float[]? curDepth;byte[]? curBgra;int dW,dH;DepthEngine? eng;
// Cached raw AI depth output (before slider post-processing) so dragging sliders in AI mode
// re-runs only the fast CPU post-process (tone/detail/contrast), not the neural network.
float[]? aiRawDepth;int aiRawW,aiRawH;
// AI background-removal mask (SegmentationEngine). Cached per-image (keyed by bgMaskPath).
SegmentationEngine? seg;float[]? bgMask;string? bgMaskPath;
bool autoCropApplied;
// Normal / AO maps — both derived from curDepth, not separate AI models.
byte[]? curNormalBgra;int normalW,normalH;
byte[]? curAOGray;int aoW,aoH;
// Which map the big preview is showing: "depth" | "normal" | "ao" | "3d".
string activeMap="depth";
BitmapSource? depthBitmap;BitmapSource? normalBitmap;BitmapSource? aoBitmap;
static readonly Brush ActiveThumbBrush=new SolidColorBrush(Color.FromRgb(0x8B,0x5C,0xF6));
static readonly Brush InactiveThumbBrush=new SolidColorBrush(Color.FromRgb(0x33,0x33,0x33));
readonly DispatcherTimer liveTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(60)};
CancellationTokenSource? liveCts;
CancellationTokenSource? normalCts;
CancellationTokenSource? aoCts;
// --- 3D preview camera state (simple orbit: yaw/pitch/distance around the mesh center) ---
double camYaw=45,camPitch=25,camDistance=300;
Point3D meshCenter;
Point? lastMousePos;
[DllImport("shell32.dll")]static extern void DragAcceptFiles(IntPtr h,bool f);
[DllImport("shell32.dll")]static extern uint DragQueryFile(IntPtr h,uint i,StringBuilder? b,uint c);
[DllImport("shell32.dll")]static extern void DragFinish(IntPtr h);
const int WM_DROP=0x0233;
public MainWindow(){InitializeComponent();Loaded+=OnLoaded;SourceInitialized+=(s,e)=>{try{var hwnd=new WindowInteropHelper(this).Handle;var src=HwndSource.FromHwnd(hwnd);src?.AddHook(WndProc);DragAcceptFiles(hwnd,true);}catch{}};liveTimer.Tick+=(s,e)=>{liveTimer.Stop();DoLiveUpdate();};}
IntPtr WndProc(IntPtr h,int m,IntPtr w,IntPtr l,ref bool hd){if(m==WM_DROP){try{uint c=DragQueryFile(w,0xFFFFFFFF,null,0);if(c>0){var sb=new StringBuilder(1024);DragQueryFile(w,0,sb,(uint)sb.Capacity);Dispatcher.Invoke(()=>Load(sb.ToString()));}DragFinish(w);hd=true;}catch{}}return IntPtr.Zero;}
async void OnLoaded(object s,RoutedEventArgs e){try{bool admin=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);if(admin) DragDebugText.Text="Admin - drag blocked!";}catch{}GpuStatusText.Text="Ready V7.20 Checklist";eng=new DepthEngine();var st=await eng.InitializeAsync();GpuStatusText.Text=st;seg=new SegmentationEngine();await seg.InitializeAsync();AiModelCheck.IsChecked=false;UseLabCheck.IsChecked=true;PercentileCheck.IsChecked=true;RefreshPresetSlotLabels();}
bool DragOk(DragEventArgs e){if(e.Data.GetDataPresent(DataFormats.FileDrop)){var f=e.Data.GetData(DataFormats.FileDrop) as string[];if(f!=null&&f.Length>0){string ext=Path.GetExtension(f[0]).ToLower();if(new[]{".png",".jpg",".jpeg",".bmp",".tiff",".tga",".webp"}.Contains(ext)){e.Effects=DragDropEffects.Copy;e.Handled=true;DragDebugText.Text=$"Hover {Path.GetFileName(f[0])}";return true;}}}e.Effects=DragDropEffects.None;e.Handled=true;return false;}
void Window_DragEnter(object s,DragEventArgs e){DragOk(e);}void Window_DragOver(object s,DragEventArgs e){DragOk(e);}
void Window_Drop(object s,DragEventArgs e){try{if(e.Data.GetData(DataFormats.FileDrop) is string[] f&&f.Length>0) Load(f[0]);}catch(Exception ex){MessageBox.Show(ex.Message);}e.Handled=true;}
void DropZone_DragEnter(object s,DragEventArgs e){DragOk(e);}void DropZone_DragOver(object s,DragEventArgs e){DragOk(e);}
void DropZone_Drop(object s,DragEventArgs e){try{if(e.Data.GetData(DataFormats.FileDrop) is string[] f&&f.Length>0) Load(f[0]);}catch(Exception ex){MessageBox.Show(ex.Message);}e.Handled=true;}
void DropZone_MouseDown(object s,MouseButtonEventArgs e){var d=new OpenFileDialog{Filter="Images|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tga;*.webp"};if(d.ShowDialog()==true) Load(d.FileName);}
async void Load(string path){try{
curPath=path;aiRawDepth=null;bgMask=null;bgMaskPath=null;autoCropApplied=false;
ClearNormalMap();ClearAOMap();
var bmp=new BitmapImage(new Uri(path));InputPreview.Source=bmp;InputPreview.Visibility=Visibility.Visible;DropText.Visibility=Visibility.Collapsed;var wb=new WriteableBitmap(bmp);wb=new WriteableBitmap(new FormatConvertedBitmap(wb,PixelFormats.Bgra32,null,0));int st=wb.PixelWidth*4;byte[] pix=new byte[wb.PixelHeight*st];wb.CopyPixels(pix,st,0);curBgra=pix;dW=wb.PixelWidth;dH=wb.PixelHeight;GenerateBtn.IsEnabled=true;GpuStatusText.Text=$"{Path.GetFileName(path)} {dW}x{dH}";
if(RemoveBgCheck.IsChecked==true) await EnsureBgMaskAsync();
if(AiModelCheck.IsChecked==false) Reproc();
if(bgMask!=null) ScheduleLiveUpdate();
}catch(Exception ex){MessageBox.Show(ex.Message);}}
// Computes (and caches) the AI background mask for the current image, and applies auto-crop
// immediately afterward if that checkbox is on — cropping happens once, right after Load(),
// before any depth generation, so nothing downstream (aiRawDepth, normal/AO maps) can go stale
// relative to it. No-op if no bg_remove.onnx is loaded; callers fall back to alpha-channel
// removal automatically inside ImageProcessor when bgMask stays null.
async Task EnsureBgMaskAsync(){
if(seg==null||!seg.IsLoaded) return;
if(curPath==null) return;
if(bgMask!=null&&bgMaskPath==curPath) return;
string path=curPath;
try{
var result=await seg.ComputeMaskAsync(path);
if(curPath!=path) return; // image changed while we were computing
bgMask=result.Mask;bgMaskPath=path;
if(AutoCropCheck.IsChecked==true&&!autoCropApplied&&result.Width==dW&&result.Height==dH) ApplyAutoCropIfNeeded();
}catch(Exception ex){
MessageBox.Show($"AI background removal failed, falling back to alpha-channel removal: {ex.Message}");
}
}
async void RemoveBg_Changed(object s,RoutedEventArgs e){
if(RemoveBgCheck.IsChecked==true) await EnsureBgMaskAsync();
ScheduleLiveUpdate();
}
// Crops curBgra (and bgMask alongside it) to the bounding box of the AI subject mask plus a
// small margin. Destructive: the uncropped working copy isn't kept around, so unchecking
// Auto-Crop later does NOT restore the full frame — reload the source file for that. Runs
// synchronously on the UI thread (same as Reproc already does for full-res processing), since
// it only needs to run once per image right after Load(), not on every interaction.
void ApplyAutoCropIfNeeded(){
if(autoCropApplied) return;
if(bgMask==null||curBgra==null) return;
int w=dW,h=dH;
if(bgMask.Length!=w*h) return;
int minX=w,minY=h,maxX=-1,maxY=-1;
for(int y=0;y<h;y++){
int row=y*w;
for(int x=0;x<w;x++){
if(bgMask[row+x]>0.5f){
if(x<minX) minX=x; if(x>maxX) maxX=x;
if(y<minY) minY=y; if(y>maxY) maxY=y;
}
}
}
autoCropApplied=true;
if(maxX<minX||maxY<minY) return; // empty mask — nothing to crop to, leave image as-is
int marginX=(int)((maxX-minX)*0.05)+4,marginY=(int)((maxY-minY)*0.05)+4;
minX=Math.Max(0,minX-marginX);minY=Math.Max(0,minY-marginY);
maxX=Math.Min(w-1,maxX+marginX);maxY=Math.Min(h-1,maxY+marginY);
int newW=maxX-minX+1,newH=maxY-minY+1;
if(newW<=0||newH<=0||(newW==w&&newH==h)) return;
var newBgra=new byte[newW*newH*4];
var newMask=new float[newW*newH];
for(int y=0;y<newH;y++){
int sy=minY+y;
Buffer.BlockCopy(curBgra!,(sy*w+minX)*4,newBgra,y*newW*4,newW*4);
Array.Copy(bgMask,sy*w+minX,newMask,y*newW,newW);
}
curBgra=newBgra;dW=newW;dH=newH;bgMask=newMask;
GpuStatusText.Text=$"Auto-cropped to {newW}x{newH}";
}
async void AutoCrop_Changed(object s,RoutedEventArgs e){
if(AutoCropCheck.IsChecked!=true||curPath==null) return;
await EnsureBgMaskAsync();
if(!autoCropApplied) ApplyAutoCropIfNeeded();
aiRawDepth=null;ClearNormalMap();ClearAOMap();
if(AiModelCheck.IsChecked==true) Generate_Click(this,new RoutedEventArgs());
else ScheduleLiveUpdate();
}
async void Generate_Click(object s,RoutedEventArgs e){
float fl=(float)FlattenSlider.Value,flR=(float)FlattenRadiusSlider.Value,lowF=(float)LowFreqSlider.Value,midF=(float)MidFreqSlider.Value,highF=(float)HighFreqSlider.Value,det=(float)DetailSlider.Value,gam=(float)GammaSlider.Value,str=(float)StrengthSlider.Value,hi=(float)HighlightsSlider.Value,mid=(float)MidtonesSlider.Value,sh=(float)ShadowsSlider.Value;bool inv=InvertCheck.IsChecked==true,rem=RemoveBgCheck.IsChecked==true,hq=HighQualityCheck.IsChecked==true,ai=AiModelCheck.IsChecked==true,lab=UseLabCheck.IsChecked==true,seam=SeamlessCheck.IsChecked==true;float seamB=(float)SeamBlendSlider.Value;bool zeroMid=ZeroMidGrayCheck.IsChecked==true;float zeroL=(float)ZeroLevelSlider.Value+(zeroMid?0.5f:0f);bool perc=PercentileCheck.IsChecked==true;
if(rem) await EnsureBgMaskAsync();
var bgra=curBgra;int w=dW,h=dH;string? p=curPath;var mask=bgMask;
try{
if(p==null) return;
if(!ai){if(bgra==null) return;GenerateBtn.Content="PROCESSING...";GenerateBtn.IsEnabled=false;ShowProgress("Processing relief...");float[] proc=null!;await Task.Run(()=>{proc=ImageProcessor.ProcessTextureAtlasAdvanced(bgra!,w,h,det,gam,inv,hi,mid,sh,rem,lab,fl,flR,lowF,midF,highF,seam,seamB,zeroMid,zeroL,perc,0.02f,0.98f,mask);});curDepth=proc;SetDepthBitmap(ImageProcessor.FloatArrayToBitmapSource(proc,w,h));GenerateBtn.Content="DONE";GenerateBtn.IsEnabled=true;EnableSaveButtons();HideProgress();return;}
GenerateBtn.Content="GENERATING...";GenerateBtn.IsEnabled=false;
ShowProgress(hq?"Running AI depth (HQ Tiled)...":"Running AI depth...");
var uiProgress=new Progress<double>(v=>{RenderProgressBar.Value=v;ProgressLabel.Text=v<0.15?"Running AI depth (global pass)...":v<0.95?$"Running AI depth — tiling detail ({(int)(v*100)}%)...":"Finishing...";});
var res=await eng!.EstimateDepthAsync(p!,hq,uiProgress);
aiRawDepth=res.Depth;aiRawW=res.Width;aiRawH=res.Height;
var proc2=ImageProcessor.ProcessForSculptOKQuality(res.Depth,res.Width,res.Height,bgra,str,det,lowF,midF,highF,gam,inv,hi,mid,sh,zeroMid,zeroL,rem,mask,fl,flR);
curDepth=proc2;dW=res.Width;dH=res.Height;SetDepthBitmap(ImageProcessor.FloatArrayToBitmapSource(proc2,dW,dH));
GenerateBtn.Content="DONE";GenerateBtn.IsEnabled=true;EnableSaveButtons();
}catch(Exception ex){Logger.LogException("UI: Generate_Click",ex);MessageBox.Show(ex.Message);GenerateBtn.Content="FAILED";GenerateBtn.IsEnabled=true;}
finally{HideProgress();}}
void ShowProgress(string label){ProgressPanel.Visibility=Visibility.Visible;ProgressLabel.Text=label;RenderProgressBar.Value=0;}
void HideProgress(){ProgressPanel.Visibility=Visibility.Collapsed;}
void EnableSaveButtons(){Save8PngBtn.IsEnabled=true;SavePngBtn.IsEnabled=true;SaveExrBtn.IsEnabled=true;SaveTiffBtn.IsEnabled=true;SaveStlBtn.IsEnabled=true;SaveObjBtn.IsEnabled=true;GenerateNormalBtn.IsEnabled=true;GenerateAOBtn.IsEnabled=true;SaveAllBtn.IsEnabled=true;}
void Mode_Changed(object s,RoutedEventArgs? e){if(AiModelCheck==null) return;GenerateBtn.Content=AiModelCheck.IsChecked==true?"AI DEPTH":"RELIEF";}
// Swaps the loaded ONNX model (Small/Base/Large/Pro) at runtime. Only replaces the active
// session on success (DepthEngine.LoadModelAsync) so an unavailable file just shows a status
// message and keeps whichever model was working before.
// NOTE on "Pro": mechanically wired the same as Base/Large, but Apple's Depth Pro may use
// different preprocessing (normalization constants, input convention) than Depth-Anything's
// ImageNet mean/std that DepthEngine currently hardcodes — I haven't been able to verify this
// against the actual model file in this environment. If Pro produces garbled output rather
// than a clean depth map, that mismatch is the likely cause and I can fix it once you tell me
// what you're seeing.
async void ModelSize_Changed(object s,RoutedEventArgs e){
if(eng==null) return;
string file=ModelBaseRadio.IsChecked==true?"model_base.onnx":ModelLargeRadio.IsChecked==true?"model_large.onnx":ModelProRadio.IsChecked==true?"model_pro.onnx":"model.onnx";
Logger.Log($"UI: ModelSize_Changed -> {file}");
bool wasEnabled=GenerateBtn.IsEnabled;GenerateBtn.IsEnabled=false;
GpuStatusText.Text=$"Loading {file}...";
var status=await eng.LoadModelAsync(file);
GpuStatusText.Text=status;
GenerateBtn.IsEnabled=wasEnabled;
aiRawDepth=null;
ClearNormalMap();ClearAOMap();
if(curPath!=null&&AiModelCheck.IsChecked==true) Generate_Click(this,new RoutedEventArgs());
}
void Reproc(){try{if(AiModelCheck.IsChecked==true) return;if(curBgra==null) return;float fl=(float)FlattenSlider.Value,flR=(float)FlattenRadiusSlider.Value,lowF=(float)LowFreqSlider.Value,midF=(float)MidFreqSlider.Value,highF=(float)HighFreqSlider.Value,det=(float)DetailSlider.Value,gam=(float)GammaSlider.Value,hi=(float)HighlightsSlider.Value,mid=(float)MidtonesSlider.Value,sh=(float)ShadowsSlider.Value;bool inv=InvertCheck.IsChecked==true,rem=RemoveBgCheck.IsChecked==true,lab=UseLabCheck.IsChecked==true,seam=SeamlessCheck.IsChecked==true;float seamB=(float)SeamBlendSlider.Value;bool zeroMid=ZeroMidGrayCheck.IsChecked==true;float zeroL=(float)ZeroLevelSlider.Value+(zeroMid?0.5f:0f);bool perc=PercentileCheck.IsChecked==true;var proc=ImageProcessor.ProcessTextureAtlasAdvanced(curBgra!,dW,dH,det,gam,inv,hi,mid,sh,rem,lab,fl,flR,lowF,midF,highF,seam,seamB,zeroMid,zeroL,perc,0.02f,0.98f,bgMask);curDepth=proc;SetDepthBitmap(ImageProcessor.FloatArrayToBitmapSource(proc,dW,dH));EnableSaveButtons();}catch{}}
void ReprocAiLive(){
if(aiRawDepth==null) return;
float str=(float)StrengthSlider.Value,det=(float)DetailSlider.Value,lowF=(float)LowFreqSlider.Value,midF=(float)MidFreqSlider.Value,highF=(float)HighFreqSlider.Value,gam=(float)GammaSlider.Value,hi=(float)HighlightsSlider.Value,mid=(float)MidtonesSlider.Value,sh=(float)ShadowsSlider.Value,fl=(float)FlattenSlider.Value,flR=(float)FlattenRadiusSlider.Value;
bool inv=InvertCheck.IsChecked==true;bool rem=RemoveBgCheck.IsChecked==true;bool zeroMid=ZeroMidGrayCheck.IsChecked==true;float zeroL=(float)ZeroLevelSlider.Value+(zeroMid?0.5f:0f);
var depth=aiRawDepth;int w=aiRawW,h=aiRawH;var bgra=curBgra;var mask=bgMask;
liveCts?.Cancel();var cts=new CancellationTokenSource();liveCts=cts;
Task.Run(()=>{
if(cts.IsCancellationRequested) return;
var proc=ImageProcessor.ProcessForSculptOKQuality(depth!,w,h,bgra,str,det,lowF,midF,highF,gam,inv,hi,mid,sh,zeroMid,zeroL,rem,mask,fl,flR);
if(cts.IsCancellationRequested) return;
Dispatcher.Invoke(()=>{
if(cts.IsCancellationRequested) return;
curDepth=proc;dW=w;dH=h;SetDepthBitmap(ImageProcessor.FloatArrayToBitmapSource(proc,w,h));
EnableSaveButtons();
});
});
}
void ScheduleLiveUpdate(){liveTimer.Stop();liveTimer.Start();}
void DoLiveUpdate(){if(AiModelCheck.IsChecked==true) ReprocAiLive();else Reproc();}
void Toggle_Changed(object s,RoutedEventArgs e){ScheduleLiveUpdate();}
void Slider_ValueChanged(object s,RoutedPropertyChangedEventArgs<double> e){if(StrengthLabel!=null) StrengthLabel.Text=StrengthSlider.Value.ToString("0.0");if(DetailLabel!=null) DetailLabel.Text=DetailSlider.Value.ToString("0.00");if(GammaLabel!=null) GammaLabel.Text=GammaSlider.Value.ToString("0.00");if(ShadowsLabel!=null) ShadowsLabel.Text=ShadowsSlider.Value.ToString("0.00");if(MidtonesLabel!=null) MidtonesLabel.Text=MidtonesSlider.Value.ToString("0.00");if(HighlightsLabel!=null) HighlightsLabel.Text=HighlightsSlider.Value.ToString("0.00");if(FlattenLabel!=null) FlattenLabel.Text=FlattenSlider.Value.ToString("0.00");if(FlattenRadiusLabel!=null) FlattenRadiusLabel.Text=FlattenRadiusSlider.Value.ToString("0");if(LowFreqLabel!=null) LowFreqLabel.Text=LowFreqSlider.Value.ToString("0.00");if(MidFreqLabel!=null) MidFreqLabel.Text=MidFreqSlider.Value.ToString("0.00");if(HighFreqLabel!=null) HighFreqLabel.Text=HighFreqSlider.Value.ToString("0.00");if(SeamBlendLabel!=null) SeamBlendLabel.Text=SeamBlendSlider.Value.ToString("0.00");if(ZeroLevelLabel!=null) ZeroLevelLabel.Text=ZeroLevelSlider.Value.ToString("0.00");if(AiModelCheck!=null&&((AiModelCheck.IsChecked==false&&curBgra!=null)||(AiModelCheck.IsChecked==true&&aiRawDepth!=null))) ScheduleLiveUpdate();}
// --- Multi-map preview: thumbnail strip + tiled-preview toggle -----------------------------
void ShowFlatPreview(){OutputImage.Visibility=Visibility.Visible;PreviewViewport.Visibility=Visibility.Collapsed;}
void RefreshMainView(){
if(activeMap=="3d") return;
BitmapSource? bmp=activeMap=="normal"?normalBitmap:activeMap=="ao"?aoBitmap:depthBitmap;
if(bmp==null) return;
OutputImage.Source=(TilePreviewCheck.IsChecked==true)?BuildTiledPreview(bmp):bmp;
}
void TilePreview_Changed(object s,RoutedEventArgs e){RefreshMainView();}
// Composites a 2x2 tiled version of a bitmap purely for on-screen preview (verifying Seamless
// blending) — never touches what actually gets saved to disk.
static BitmapSource BuildTiledPreview(BitmapSource src){
int w=src.PixelWidth,h=src.PixelHeight;
var dv=new DrawingVisual();
using(var dc=dv.RenderOpen()){
for(int ty=0;ty<2;ty++) for(int tx=0;tx<2;tx++) dc.DrawImage(src,new Rect(tx*w,ty*h,w,h));
}
var rtb=new RenderTargetBitmap(w*2,h*2,96,96,PixelFormats.Pbgra32);
rtb.Render(dv);
rtb.Freeze();
return rtb;
}
void SetDepthBitmap(BitmapSource bmp){depthBitmap=bmp;DepthThumb.Source=bmp;if(activeMap=="depth") RefreshMainView();}
void SetNormalBitmap(BitmapSource bmp){normalBitmap=bmp;NormalThumb.Source=bmp;NormalThumbBorder.Visibility=Visibility.Visible;if(activeMap=="normal") RefreshMainView();}
void SetAOBitmap(BitmapSource bmp){aoBitmap=bmp;AOThumb.Source=bmp;AOThumbBorder.Visibility=Visibility.Visible;if(activeMap=="ao") RefreshMainView();}
void ClearNormalMap(){curNormalBgra=null;normalBitmap=null;NormalThumbBorder.Visibility=Visibility.Collapsed;NormalThumb.Source=null;SaveNormalBtn.IsEnabled=false;if(activeMap=="normal"){activeMap="depth";ShowFlatPreview();RefreshMainView();}HighlightThumbs();}
void ClearAOMap(){curAOGray=null;aoBitmap=null;AOThumbBorder.Visibility=Visibility.Collapsed;AOThumb.Source=null;SaveAOBtn.IsEnabled=false;if(activeMap=="ao"){activeMap="depth";ShowFlatPreview();RefreshMainView();}HighlightThumbs();}
void HighlightThumbs(){DepthThumbBorder.BorderBrush=activeMap=="depth"?ActiveThumbBrush:InactiveThumbBrush;NormalThumbBorder.BorderBrush=activeMap=="normal"?ActiveThumbBrush:InactiveThumbBrush;AOThumbBorder.BorderBrush=activeMap=="ao"?ActiveThumbBrush:InactiveThumbBrush;ThreeDTileBorder.BorderBrush=activeMap=="3d"?ActiveThumbBrush:InactiveThumbBrush;}
void DepthThumb_Click(object s,MouseButtonEventArgs e){activeMap="depth";ShowFlatPreview();RefreshMainView();HighlightThumbs();}
void NormalThumb_Click(object s,MouseButtonEventArgs e){if(normalBitmap==null) return;activeMap="normal";ShowFlatPreview();RefreshMainView();HighlightThumbs();}
void AOThumb_Click(object s,MouseButtonEventArgs e){if(aoBitmap==null) return;activeMap="ao";ShowFlatPreview();RefreshMainView();HighlightThumbs();}
// --- Normal map --------------------------------------------------------------------------
void GenerateNormal_Click(object s,RoutedEventArgs e){RecomputeNormalMap();}
void NormalStrengthSlider_ValueChanged(object s,RoutedPropertyChangedEventArgs<double> e){if(NormalStrengthLabel!=null) NormalStrengthLabel.Text=NormalStrengthSlider.Value.ToString("0.0");if(curNormalBgra!=null) RecomputeNormalMap();}
void InvertNormalY_Changed(object s,RoutedEventArgs e){if(curNormalBgra!=null) RecomputeNormalMap();}
void RecomputeNormalMap(){
if(curDepth==null) return;
float strength=(float)NormalStrengthSlider.Value;bool invY=InvertNormalYCheck.IsChecked==true;bool edgeSmooth=EdgePreserveSmoothCheck.IsChecked==true;
var depth=curDepth;int w=dW,h=dH;
normalCts?.Cancel();var cts=new CancellationTokenSource();normalCts=cts;
Task.Run(()=>{
if(cts.IsCancellationRequested) return;
var bgra=ImageProcessor.ComputeNormalMap(depth!,w,h,strength,invY,edgeSmooth);
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
// --- Cavity / AO map -----------------------------------------------------------------------
void GenerateAO_Click(object s,RoutedEventArgs e){RecomputeAOMap();}
void AOStrengthSlider_ValueChanged(object s,RoutedPropertyChangedEventArgs<double> e){if(AOStrengthLabel!=null) AOStrengthLabel.Text=AOStrengthSlider.Value.ToString("0.0");if(curAOGray!=null) RecomputeAOMap();}
void AOBlurSlider_ValueChanged(object s,RoutedPropertyChangedEventArgs<double> e){if(AOBlurLabel!=null) AOBlurLabel.Text=AOBlurSlider.Value.ToString("0");if(curAOGray!=null) RecomputeAOMap();}
void RecomputeAOMap(){
if(curDepth==null) return;
float strength=(float)AOStrengthSlider.Value;int blur=(int)AOBlurSlider.Value;
var depth=curDepth;int w=dW,h=dH;
aoCts?.Cancel();var cts=new CancellationTokenSource();aoCts=cts;
Task.Run(()=>{
if(cts.IsCancellationRequested) return;
var gray=ImageProcessor.ComputeCavityMap(depth!,w,h,strength,blur);
if(cts.IsCancellationRequested) return;
Dispatcher.Invoke(()=>{
if(cts.IsCancellationRequested) return;
curAOGray=gray;aoW=w;aoH=h;
SetAOBitmap(ImageProcessor.GrayArrayToBitmapSource(gray,w,h));
SaveAOBtn.IsEnabled=true;SaveAllBtn.IsEnabled=true;
});
});
}
void SaveAO_Click(object s,RoutedEventArgs e){if(curAOGray==null) return;var d=new SaveFileDialog{Filter="PNG|*.png",FileName="ao_map.png"};if(d.ShowDialog()==true) ImageProcessor.SaveCavityMapPng(curAOGray,aoW,aoH,d.FileName);}
// --- 3D preview ---------------------------------------------------------------------------
// Builds a WPF Media3D mesh from the same downsampled height grid StlExporter uses for STL/OBJ
// export (StlExporter.BuildHeightGrid), so what you see rotating here matches what gets saved,
// not a separately-approximated preview. Height scale (10) matches the default used by
// SaveAsStl/SaveAsObj's Save buttons. Vertex normals are a simple finite-difference estimate on
// the coarse grid — good enough for shading a preview, not exported anywhere.
void ThreeDTile_Click(object s,MouseButtonEventArgs e){
if(curDepth==null) return;
BuildPreviewMesh();
activeMap="3d";
OutputImage.Visibility=Visibility.Collapsed;
PreviewViewport.Visibility=Visibility.Visible;
HighlightThumbs();
}
void BuildPreviewMesh(){
if(curDepth==null) return;
var heights=StlExporter.BuildHeightGrid(curDepth!,dW,dH,out int gridW,out int gridH);
const float heightScale=10f;
// Separate, gentler sensitivity for the shading normals than for the actual mesh height.
// Reusing heightScale (10) directly here made steep/high-frequency areas (fine detail, sharp
// edges) tip the computed normal drastically relative to the fixed Y=2 baseline, which under
// a single hard directional light could swing those patches to near-black or oddly lit —
// visible as a harsh, almost "inverted-looking" patch next to normally-lit areas.
const float normalGradientScale=2.5f;
var positions=new Point3DCollection(gridW*gridH);
var normals=new Vector3DCollection(gridW*gridH);
var uvs=new PointCollection(gridW*gridH);
for(int gy=0;gy<gridH;gy++){
for(int gx=0;gx<gridW;gx++){
positions.Add(new Point3D(gx,heights[gx,gy]*heightScale,gy)); // Y-up for the viewport; STL/OBJ files are unaffected by this, they use their own convention
float hL=heights[Math.Max(gx-1,0),gy],hR=heights[Math.Min(gx+1,gridW-1),gy];
float hD=heights[gx,Math.Max(gy-1,0)],hU=heights[gx,Math.Min(gy+1,gridH-1)];
var n=new Vector3D(-(hR-hL)*normalGradientScale,2,-(hU-hD)*normalGradientScale);
if(n.Length>1e-6) n.Normalize(); else n=new Vector3D(0,1,0);
normals.Add(n);
// WPF images/brushes are Y-down already (same as the source pixel data), so no V-flip needed
// here — texture coordinates map 1:1 with the grid's own row order.
uvs.Add(new Point(gridW>1?gx/(double)(gridW-1):0,gridH>1?gy/(double)(gridH-1):0));
}
}
var indices=new Int32Collection();
for(int gy=0;gy<gridH-1;gy++){
for(int gx=0;gx<gridW-1;gx++){
int i00=gy*gridW+gx,i10=i00+1,i01=i00+gridW,i11=i01+1;
indices.Add(i00);indices.Add(i10);indices.Add(i11);
indices.Add(i00);indices.Add(i11);indices.Add(i01);
}
}
PreviewMeshModel.Geometry=new MeshGeometry3D{Positions=positions,Normals=normals,TriangleIndices=indices,TextureCoordinates=uvs};
if(depthBitmap!=null){
var brush=new ImageBrush(depthBitmap){ViewportUnits=BrushMappingMode.RelativeToBoundingBox};
PreviewMeshMaterial.Brush=brush;
PreviewMeshBackMaterial.Brush=brush;
}
meshCenter=new Point3D(gridW/2.0,heights[gridW/2,gridH/2]*heightScale,gridH/2.0);
camDistance=Math.Max(gridW,gridH)*1.4;camYaw=45;camPitch=25;
UpdateCamera();
}
void UpdateCamera(){
double yawRad=camYaw*Math.PI/180,pitchRad=camPitch*Math.PI/180;
double x=meshCenter.X+camDistance*Math.Cos(pitchRad)*Math.Sin(yawRad);
double y=meshCenter.Y+camDistance*Math.Sin(pitchRad);
double z=meshCenter.Z+camDistance*Math.Cos(pitchRad)*Math.Cos(yawRad);
PreviewCamera.Position=new Point3D(x,y,z);
PreviewCamera.LookDirection=new Vector3D(meshCenter.X-x,meshCenter.Y-y,meshCenter.Z-z);
PreviewCamera.UpDirection=new Vector3D(0,1,0);
}
void Viewport_MouseDown(object s,MouseButtonEventArgs e){lastMousePos=e.GetPosition(PreviewViewport);Mouse.Capture(PreviewViewport);}
void Viewport_MouseUp(object s,MouseButtonEventArgs e){lastMousePos=null;Mouse.Capture(null);}
void Viewport_MouseMove(object s,MouseEventArgs e){
if(lastMousePos==null||e.LeftButton!=MouseButtonState.Pressed) return;
var pos=e.GetPosition(PreviewViewport);
var delta=pos-lastMousePos.Value;
camYaw-=delta.X*0.4;camPitch=Math.Clamp(camPitch+delta.Y*0.4,-85,85);
lastMousePos=pos;
UpdateCamera();
}
void Viewport_MouseWheel(object s,MouseWheelEventArgs e){camDistance=Math.Clamp(camDistance*(e.Delta>0?0.9:1.1),20,5000);UpdateCamera();}
// --- Mesh export ---------------------------------------------------------------------------
void SaveObj_Click(object s,RoutedEventArgs e){if(curDepth==null) return;var d=new SaveFileDialog{Filter="OBJ|*.obj",FileName="relief.obj"};if(d.ShowDialog()==true) StlExporter.SaveAsObj(curDepth,dW,dH,d.FileName,10f);}
// --- Save all --------------------------------------------------------------------------------
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
StlExporter.SaveAsObj(curDepth,dW,dH,Path.Combine(dir,$"{baseName}_relief.obj"),10f);
if(curNormalBgra!=null) ImageProcessor.SaveNormalMapPng(curNormalBgra,normalW,normalH,Path.Combine(dir,$"{baseName}_normal.png"));
if(curAOGray!=null) ImageProcessor.SaveCavityMapPng(curAOGray,aoW,aoH,Path.Combine(dir,$"{baseName}_ao.png"));
MessageBox.Show($"Saved to {dir}");
}catch(Exception ex){MessageBox.Show($"Save All failed: {ex.Message}");}
}
// --- Presets ---------------------------------------------------------------------------------
sealed class Preset{
public double Flatten{get;set;} public double FlattenRadius{get;set;} public double LowFreq{get;set;} public double MidFreq{get;set;} public double HighFreq{get;set;} public double Detail{get;set;} public double Gamma{get;set;} public double Shadows{get;set;} public double Midtones{get;set;} public double Highlights{get;set;} public double SeamBlend{get;set;} public double ZeroLevel{get;set;} public double Strength{get;set;} public double NormalStrength{get;set;} public double AOStrength{get;set;} public double AOBlur{get;set;}
public bool UseLab{get;set;} public bool Percentile{get;set;} public bool Seamless{get;set;} public bool ZeroMidGray{get;set;} public bool Invert{get;set;} public bool RemoveBg{get;set;} public bool AutoCrop{get;set;} public bool HighQuality{get;set;} public bool InvertNormalY{get;set;} public bool EdgePreserveSmooth{get;set;} public bool AiModel{get;set;}
}
Preset CapturePreset()=>new Preset{
Flatten=FlattenSlider.Value,FlattenRadius=FlattenRadiusSlider.Value,LowFreq=LowFreqSlider.Value,MidFreq=MidFreqSlider.Value,HighFreq=HighFreqSlider.Value,Detail=DetailSlider.Value,Gamma=GammaSlider.Value,Shadows=ShadowsSlider.Value,Midtones=MidtonesSlider.Value,Highlights=HighlightsSlider.Value,SeamBlend=SeamBlendSlider.Value,ZeroLevel=ZeroLevelSlider.Value,Strength=StrengthSlider.Value,NormalStrength=NormalStrengthSlider.Value,AOStrength=AOStrengthSlider.Value,AOBlur=AOBlurSlider.Value,
UseLab=UseLabCheck.IsChecked==true,Percentile=PercentileCheck.IsChecked==true,Seamless=SeamlessCheck.IsChecked==true,ZeroMidGray=ZeroMidGrayCheck.IsChecked==true,Invert=InvertCheck.IsChecked==true,RemoveBg=RemoveBgCheck.IsChecked==true,AutoCrop=AutoCropCheck.IsChecked==true,HighQuality=HighQualityCheck.IsChecked==true,InvertNormalY=InvertNormalYCheck.IsChecked==true,EdgePreserveSmooth=EdgePreserveSmoothCheck.IsChecked==true,AiModel=AiModelCheck.IsChecked==true
};
void ApplyPreset(Preset p){
FlattenSlider.Value=p.Flatten;FlattenRadiusSlider.Value=p.FlattenRadius;LowFreqSlider.Value=p.LowFreq;MidFreqSlider.Value=p.MidFreq;HighFreqSlider.Value=p.HighFreq;DetailSlider.Value=p.Detail;GammaSlider.Value=p.Gamma;ShadowsSlider.Value=p.Shadows;MidtonesSlider.Value=p.Midtones;HighlightsSlider.Value=p.Highlights;SeamBlendSlider.Value=p.SeamBlend;ZeroLevelSlider.Value=p.ZeroLevel;StrengthSlider.Value=p.Strength;NormalStrengthSlider.Value=p.NormalStrength;AOStrengthSlider.Value=p.AOStrength;AOBlurSlider.Value=p.AOBlur;
UseLabCheck.IsChecked=p.UseLab;PercentileCheck.IsChecked=p.Percentile;SeamlessCheck.IsChecked=p.Seamless;ZeroMidGrayCheck.IsChecked=p.ZeroMidGray;InvertCheck.IsChecked=p.Invert;RemoveBgCheck.IsChecked=p.RemoveBg;AutoCropCheck.IsChecked=p.AutoCrop;HighQualityCheck.IsChecked=p.HighQuality;InvertNormalYCheck.IsChecked=p.InvertNormalY;EdgePreserveSmoothCheck.IsChecked=p.EdgePreserveSmooth;AiModelCheck.IsChecked=p.AiModel;
ScheduleLiveUpdate();
}
void SavePreset_Click(object s,RoutedEventArgs e){
var d=new SaveFileDialog{Filter="AnvilDepth Preset|*.json",FileName="preset.json"};
if(d.ShowDialog()!=true) return;
try{File.WriteAllText(d.FileName,JsonSerializer.Serialize(CapturePreset(),new JsonSerializerOptions{WriteIndented=true}));}
catch(Exception ex){MessageBox.Show($"Save preset failed: {ex.Message}");}
}
void LoadPreset_Click(object s,RoutedEventArgs e){
var d=new OpenFileDialog{Filter="AnvilDepth Preset|*.json"};
if(d.ShowDialog()!=true) return;
try{
var p=JsonSerializer.Deserialize<Preset>(File.ReadAllText(d.FileName));
if(p!=null) ApplyPreset(p);
}catch(Exception ex){MessageBox.Show($"Load preset failed: {ex.Message}");}
}
// Matches the default Value="..."/IsChecked="..." attributes in MainWindow.xaml — if you change
// a default there, update this to match so Reset actually resets to the shipped defaults.
static Preset DefaultPreset()=>new Preset{
Flatten=0.35,FlattenRadius=40,LowFreq=1,MidFreq=1,HighFreq=1,Detail=0.85,Gamma=1,Shadows=1,Midtones=1,Highlights=1,SeamBlend=0.3,ZeroLevel=0,Strength=1,NormalStrength=2,AOStrength=3,AOBlur=12,
UseLab=true,Percentile=true,Seamless=false,ZeroMidGray=false,Invert=false,RemoveBg=true,AutoCrop=false,HighQuality=false,InvertNormalY=false,EdgePreserveSmooth=false,AiModel=false
};
void Reset_Click(object s,RoutedEventArgs e){ApplyPreset(DefaultPreset());}
// --- Quick numbered presets (7 slots) ------------------------------------------------------
// A faster complement to the file-dialog Save/Load Preset buttons above: one click saves or
// loads a fixed slot, no dialog. Save Mode gates which behavior a click performs — this keeps
// every slot button doing exactly one thing per click rather than needing a modifier key
// (right-click/ctrl-click), which would be less discoverable, especially for the low-vision
// use case this whole panel reorganization is aimed at.
static string PresetSlotPath(int slot)=>Path.Combine(AppContext.BaseDirectory,"Presets",$"slot{slot}.json");
Button? PresetSlotButton(int slot)=>slot switch{1=>PresetSlot1Btn,2=>PresetSlot2Btn,3=>PresetSlot3Btn,4=>PresetSlot4Btn,5=>PresetSlot5Btn,6=>PresetSlot6Btn,7=>PresetSlot7Btn,_=>null};
void RefreshPresetSlotLabels(){
for(int i=1;i<=7;i++){
var btn=PresetSlotButton(i);
if(btn==null) continue;
btn.Content=File.Exists(PresetSlotPath(i))?$"Preset {i}":$"Preset {i} (empty)";
}
}
void PresetSlot_Click(object s,RoutedEventArgs e){
if(s is not Button b||b.Tag is not string tagStr||!int.TryParse(tagStr,out int slot)) return;
string path=PresetSlotPath(slot);
if(PresetSaveModeCheck.IsChecked==true){
try{
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
File.WriteAllText(path,JsonSerializer.Serialize(CapturePreset(),new JsonSerializerOptions{WriteIndented=true}));
RefreshPresetSlotLabels();
GpuStatusText.Text=$"Saved to Preset {slot}";
}catch(Exception ex){MessageBox.Show($"Save to Preset {slot} failed: {ex.Message}");}
}else{
if(!File.Exists(path)){MessageBox.Show($"Preset {slot} is empty. Check \"Save Mode\" above, then click Preset {slot} again to save your current settings into it.");return;}
try{
var p=JsonSerializer.Deserialize<Preset>(File.ReadAllText(path));
if(p!=null){ApplyPreset(p);GpuStatusText.Text=$"Loaded Preset {slot}";}
}catch(Exception ex){MessageBox.Show($"Load Preset {slot} failed: {ex.Message}");}
}
}
void OpenPresetsFolder_Click(object s,RoutedEventArgs e){
try{
string dir=Path.Combine(AppContext.BaseDirectory,"Presets");
Directory.CreateDirectory(dir);
Process.Start(new ProcessStartInfo{FileName=dir,UseShellExecute=true});
}catch(Exception ex){MessageBox.Show($"Couldn't open Presets folder: {ex.Message}");}
}
void OpenLogsFolder_Click(object s,RoutedEventArgs e){
try{
Process.Start(new ProcessStartInfo{FileName=Logger.LogFolderPath,UseShellExecute=true});
}catch(Exception ex){MessageBox.Show($"Couldn't open Logs folder: {ex.Message}");}
}
// Scales the entire sidebar (text, sliders, buttons, checkboxes — not just font size) via
// LayoutTransform, which WPF's layout system respects for measuring/arranging inside the
// ScrollViewer, so the scrollbar correctly extends to cover the enlarged content.
void LargeText_Changed(object s,RoutedEventArgs e){
SidebarPanel.LayoutTransform=LargeTextCheck.IsChecked==true?new ScaleTransform(1.3,1.3):Transform.Identity;
}
// --- Batch mode --------------------------------------------------------------------------------
// Applies the CURRENT slider/checkbox settings (captured once, at the start) to every image in
// a chosen folder — this is what "batch" means here: consistent processing across a set, not
// per-image tuning. Loads images via OpenCvSharp (not the WPF BitmapImage path Load() uses) so
// the whole loop can run off the UI thread without any thread-affinity issues.
async void BatchProcess_Click(object s,RoutedEventArgs e){
using var inFbd=new System.Windows.Forms.FolderBrowserDialog{Description="Folder of images to process"};
if(inFbd.ShowDialog()!=System.Windows.Forms.DialogResult.OK) return;
using var outFbd=new System.Windows.Forms.FolderBrowserDialog{Description="Output folder for all maps"};
if(outFbd.ShowDialog()!=System.Windows.Forms.DialogResult.OK) return;
string inDir=inFbd.SelectedPath,outDir=outFbd.SelectedPath;
var exts=new[]{".png",".jpg",".jpeg",".bmp",".tiff",".tga",".webp"};
string[] files;
try{files=Directory.GetFiles(inDir).Where(f=>exts.Contains(Path.GetExtension(f).ToLower())).ToArray();}
catch(Exception ex){MessageBox.Show($"Couldn't read folder: {ex.Message}");return;}
if(files.Length==0){MessageBox.Show("No images found in that folder.");return;}

bool ai=AiModelCheck.IsChecked==true,hq=HighQualityCheck.IsChecked==true,rem=RemoveBgCheck.IsChecked==true,autoCrop=AutoCropCheck.IsChecked==true;
float fl=(float)FlattenSlider.Value,flR=(float)FlattenRadiusSlider.Value,lowF=(float)LowFreqSlider.Value,midF=(float)MidFreqSlider.Value,highF=(float)HighFreqSlider.Value,det=(float)DetailSlider.Value,gam=(float)GammaSlider.Value,str=(float)StrengthSlider.Value,hi=(float)HighlightsSlider.Value,mid=(float)MidtonesSlider.Value,sh=(float)ShadowsSlider.Value;
bool inv=InvertCheck.IsChecked==true,lab=UseLabCheck.IsChecked==true,seam=SeamlessCheck.IsChecked==true,perc=PercentileCheck.IsChecked==true;
float seamB=(float)SeamBlendSlider.Value;bool zeroMid=ZeroMidGrayCheck.IsChecked==true;float zeroL=(float)ZeroLevelSlider.Value+(zeroMid?0.5f:0f);
float normStrength=(float)NormalStrengthSlider.Value;bool invY=InvertNormalYCheck.IsChecked==true;bool edgeSmooth=EdgePreserveSmoothCheck.IsChecked==true;
float aoStrength=(float)AOStrengthSlider.Value;int aoBlur=(int)AOBlurSlider.Value;

BatchBtn.IsEnabled=false;GenerateBtn.IsEnabled=false;
int done=0,failed=0;
foreach(var f in files){
GpuStatusText.Text=$"Batch {done+failed+1}/{files.Length}: {Path.GetFileName(f)}";
try{
var (bgra,w,h)=await Task.Run(()=>LoadImageBgra(f));
float[]? mask=null;
if(rem&&seg!=null&&seg.IsLoaded){
try{var mr=await seg.ComputeMaskAsync(f);if(mr.Width==w&&mr.Height==h) mask=mr.Mask;}catch{/* fall back to alpha-based removal for this file */}
}
if(autoCrop&&mask!=null){
var cropped=CropToMask(bgra,w,h,mask);
bgra=cropped.bgra;w=cropped.w;h=cropped.h;mask=cropped.mask;
}
float[] depth;int dw=w,dh=h;
if(ai){
var res=await eng!.EstimateDepthAsync(f,hq);
depth=await Task.Run(()=>ImageProcessor.ProcessForSculptOKQuality(res.Depth,res.Width,res.Height,bgra,str,det,lowF,midF,highF,gam,inv,hi,mid,sh,zeroMid,zeroL,rem,mask,fl,flR));
dw=res.Width;dh=res.Height;
}else{
depth=await Task.Run(()=>ImageProcessor.ProcessTextureAtlasAdvanced(bgra,w,h,det,gam,inv,hi,mid,sh,rem,lab,fl,flR,lowF,midF,highF,seam,seamB,zeroMid,zeroL,perc,0.02f,0.98f,mask));
}
string baseName=Path.GetFileNameWithoutExtension(f);
await Task.Run(()=>{
ImageProcessor.SaveAs16Bit(depth,dw,dh,Path.Combine(outDir,$"{baseName}_depth_16bit.png"));
ImageProcessor.SaveAsEXR(depth,dw,dh,Path.Combine(outDir,$"{baseName}_depth_32bit.exr"));
StlExporter.SaveAsStl(depth,dw,dh,Path.Combine(outDir,$"{baseName}_relief.stl"),10f);
var normalBgra=ImageProcessor.ComputeNormalMap(depth,dw,dh,normStrength,invY,edgeSmooth);
ImageProcessor.SaveNormalMapPng(normalBgra,dw,dh,Path.Combine(outDir,$"{baseName}_normal.png"));
var aoGray=ImageProcessor.ComputeCavityMap(depth,dw,dh,aoStrength,aoBlur);
ImageProcessor.SaveCavityMapPng(aoGray,dw,dh,Path.Combine(outDir,$"{baseName}_ao.png"));
});
done++;
}catch(Exception ex){
failed++;
System.Diagnostics.Debug.WriteLine($"Batch failed on {f}: {ex.Message}");
}
}
GpuStatusText.Text=$"Batch complete: {done} done, {failed} failed.";
BatchBtn.IsEnabled=true;GenerateBtn.IsEnabled=curPath!=null;
MessageBox.Show($"Batch complete.\n{done} succeeded, {failed} failed.\nSaved to {outDir}");
}
// Loads an image file straight to a BGRA byte array via OpenCvSharp — no WPF types involved,
// so this is safe to call from a background thread (unlike Load()'s BitmapImage-based path).
static (byte[] bgra,int w,int h) LoadImageBgra(string path){
using var mat=OpenCvSharp.Cv2.ImRead(path,OpenCvSharp.ImreadModes.Color);
if(mat.Empty()) throw new InvalidOperationException($"Could not read image: {path}");
using var bgra=new OpenCvSharp.Mat();
OpenCvSharp.Cv2.CvtColor(mat,bgra,OpenCvSharp.ColorConversionCodes.BGR2BGRA);
var result=new byte[bgra.Width*bgra.Height*4];
System.Runtime.InteropServices.Marshal.Copy(bgra.Data,result,0,result.Length);
return (result,bgra.Width,bgra.Height);
}
// Shared crop-to-mask-bounding-box logic, usable both from batch (no instance fields touched)
// and conceptually the same math as ApplyAutoCropIfNeeded uses for the interactive path.
static (byte[] bgra,int w,int h,float[] mask) CropToMask(byte[] bgra,int w,int h,float[] mask){
int minX=w,minY=h,maxX=-1,maxY=-1;
for(int y=0;y<h;y++){
int row=y*w;
for(int x=0;x<w;x++){
if(mask[row+x]>0.5f){if(x<minX)minX=x;if(x>maxX)maxX=x;if(y<minY)minY=y;if(y>maxY)maxY=y;}
}
}
if(maxX<minX||maxY<minY) return(bgra,w,h,mask);
int marginX=(int)((maxX-minX)*0.05)+4,marginY=(int)((maxY-minY)*0.05)+4;
minX=Math.Max(0,minX-marginX);minY=Math.Max(0,minY-marginY);
maxX=Math.Min(w-1,maxX+marginX);maxY=Math.Min(h-1,maxY+marginY);
int newW=maxX-minX+1,newH=maxY-minY+1;
if(newW<=0||newH<=0||(newW==w&&newH==h)) return(bgra,w,h,mask);
var newBgra=new byte[newW*newH*4];
var newMask=new float[newW*newH];
for(int y=0;y<newH;y++){
int sy=minY+y;
Buffer.BlockCopy(bgra,(sy*w+minX)*4,newBgra,y*newW*4,newW*4);
Array.Copy(mask,sy*w+minX,newMask,y*newW,newW);
}
return(newBgra,newW,newH,newMask);
}
// --- Original per-map save buttons — unchanged, still work standalone -----------------------
void Save8Png_Click(object s,RoutedEventArgs e){if(curDepth==null) return;var d=new SaveFileDialog{Filter="PNG 8-bit|*.png",FileName="depth_8bit.png"};if(d.ShowDialog()==true) ImageProcessor.SaveAs8Bit(curDepth,dW,dH,d.FileName);}
void SavePng_Click(object s,RoutedEventArgs e){if(curDepth==null) return;var d=new SaveFileDialog{Filter="PNG 16-bit|*.png",FileName="depth_16bit.png"};if(d.ShowDialog()==true) ImageProcessor.SaveAs16Bit(curDepth,dW,dH,d.FileName);}
void SaveExr_Click(object s,RoutedEventArgs e){if(curDepth==null) return;var d=new SaveFileDialog{Filter="EXR|*.exr",FileName="depth_32bit.exr"};if(d.ShowDialog()==true) ImageProcessor.SaveAsEXR(curDepth,dW,dH,d.FileName);}
void SaveTiffBtn_Click(object s,RoutedEventArgs e){if(curDepth==null) return;var d=new SaveFileDialog{Filter="TIFF|*.tiff",FileName="depth_32bit.tiff"};if(d.ShowDialog()==true) ImageProcessor.SaveAsTiff32(curDepth,dW,dH,d.FileName);}
void SaveStl_Click(object s,RoutedEventArgs e){if(curDepth==null) return;var d=new SaveFileDialog{Filter="STL|*.stl",FileName="relief.stl"};if(d.ShowDialog()==true) StlExporter.SaveAsStl(curDepth,dW,dH,d.FileName,10f);}
}
}
