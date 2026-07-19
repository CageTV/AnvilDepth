
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using AnvilDepth.Services;

namespace AnvilDepth
{
    public partial class MainWindow : System.Windows.Window
    {
        private string? currentImagePath;
        private float[]? currentDepth;
        private byte[]? currentBgra;
        private int depthW, depthH;
        private DepthEngine? depthEngine;

        [DllImport("shell32.dll")] static extern void DragAcceptFiles(IntPtr hwnd, bool fAccept);
        [DllImport("shell32.dll")] static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);
        [DllImport("shell32.dll")] static extern uint DragQueryFile(IntPtr hDrop, uint iFile, IntPtr lpszFile, uint cch);
        [DllImport("shell32.dll")] static extern void DragFinish(IntPtr hDrop);
        private const int WM_DROPFILES = 0x0233;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            SourceInitialized += (s,e) => {
                try{
                    var hwnd = new WindowInteropHelper(this).Handle;
                    var source = HwndSource.FromHwnd(hwnd);
                    source?.AddHook(WndProc);
                    DragAcceptFiles(hwnd, true);
                }catch{}
            };
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if(msg == WM_DROPFILES){
                try{
                    uint count = DragQueryFile(wParam, 0xFFFFFFFF, IntPtr.Zero, 0);
                    if(count > 0){
                        var sb = new StringBuilder(1024);
                        DragQueryFile(wParam, 0, sb, (uint)sb.Capacity);
                        string file = sb.ToString();
                        Dispatcher.Invoke(()=> LoadImage(file));
                    }
                    DragFinish(wParam);
                    handled = true;
                }catch{}
            }
            return IntPtr.Zero;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            GpuStatusText.Text = "Ready - DDS BC7 supported";
            depthEngine = new DepthEngine();
            var status = await depthEngine.InitializeAsync();
            GpuStatusText.Text = status + " | DDS + PNG";
            AiModelCheck.IsChecked = false;
            Mode_Changed(this, null);
        }

        // Window-level drag (fixes when dragging over empty space)
        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                LoadImage(files[0]);
        }

        // DropZone drag (your working v1)
        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                LoadImage(files[0]);
        }
        private void DropZone_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tga;*.webp;*.dds|DDS (BC7 supported)|*.dds|All|*.*" };
            if (dlg.ShowDialog() == true) LoadImage(dlg.FileName);
        }

        private void LoadImage(string path)
        {
            try
            {
                currentImagePath = path;
                if (Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    var (bgra, w, h) = DdsCodec.Decode(path);
                    currentBgra = bgra;
                    depthW = w; depthH = h;
                    var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
                    bmp.Freeze();
                    InputPreview.Source = bmp;
                    InputPreview.Visibility = Visibility.Visible;
                    DropText.Visibility = Visibility.Collapsed;
                    GenerateBtn.IsEnabled = true;
                    GpuStatusText.Text = $"{Path.GetFileName(path)} {depthW}x{depthH} [DDS BC7 OK]";
                    if (AiModelCheck.IsChecked == false) ReprocessAndShow();
                    return;
                }
                var bmp2 = new BitmapImage(new Uri(path));
                InputPreview.Source = bmp2;
                InputPreview.Visibility = Visibility.Visible;
                DropText.Visibility = Visibility.Collapsed;
                var wb = new WriteableBitmap(bmp2);
                wb = new WriteableBitmap(new FormatConvertedBitmap(wb, PixelFormats.Bgra32, null, 0));
                int stride = wb.PixelWidth * 4;
                byte[] pixels = new byte[wb.PixelHeight * stride];
                wb.CopyPixels(pixels, stride, 0);
                currentBgra = pixels;
                depthW = wb.PixelWidth;
                depthH = wb.PixelHeight;
                GenerateBtn.IsEnabled = true;
                GpuStatusText.Text = $"{Path.GetFileName(path)} {depthW}x{depthH}";
                if (AiModelCheck.IsChecked == false && currentBgra != null) ReprocessAndShow();
            }
            catch (Exception ex) { MessageBox.Show($"Load failed: {ex.Message}"); }
        }

        private async void Generate_Click(object sender, RoutedEventArgs e)
        {
            float detailVal = (float)DetailSlider.Value;
            float gammaVal = (float)GammaSlider.Value;
            float strengthVal = (float)StrengthSlider.Value;
            float highlightsVal = (float)HighlightsSlider.Value;
            float midtonesVal = (float)MidtonesSlider.Value;
            float shadowsVal = (float)ShadowsSlider.Value;
            bool invertVal = InvertCheck.IsChecked == true;
            bool removeBgVal = RemoveBgCheck.IsChecked == true;
            bool highQualityVal = HighQualityCheck.IsChecked == true;
            bool useAiModel = AiModelCheck.IsChecked == true;
            byte[]? bgraCopy = currentBgra;
            int wCopy = depthW, hCopy = depthH;
            string? pathCopy = currentImagePath;
            try
            {
                if (pathCopy == null) return;
                if (!useAiModel)
                {
                    if (bgraCopy == null) return;
                    GenerateBtn.Content = "PROCESSING..."; GenerateBtn.IsEnabled = false;
                    float[] processed = null!;
                    await Task.Run(() => { processed = ImageProcessor.ProcessTextureAtlas(bgraCopy!, wCopy, hCopy, detailVal, gammaVal, invertVal, highlightsVal, midtonesVal, shadowsVal, removeBgVal); });
                    currentDepth = processed;
                    OutputImage.Source = ImageProcessor.FloatArrayToBitmapSource(processed, wCopy, hCopy);
                    GenerateBtn.Content = "DONE"; GenerateBtn.IsEnabled = true;
                    SavePngBtn.IsEnabled = true; SaveExrBtn.IsEnabled = true; SaveStlBtn.IsEnabled = true; SaveDdsCompressedBtn.IsEnabled = true; SaveDdsUncompressedBtn.IsEnabled = true;
                    return;
                }
                GenerateBtn.Content = "GENERATING..."; GenerateBtn.IsEnabled = false;
                string aiInputPath = pathCopy!;
                string? tempPng = null;
                if (Path.GetExtension(pathCopy).Equals(".dds", StringComparison.OrdinalIgnoreCase) && bgraCopy != null)
                {
                    tempPng = Path.Combine(Path.GetTempPath(), $"anvildepth_{Guid.NewGuid():N}.png");
                    var bmp = BitmapSource.Create(wCopy, hCopy, 96, 96, PixelFormats.Bgra32, null, bgraCopy, wCopy * 4);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (var fs = File.Create(tempPng)) enc.Save(fs);
                    aiInputPath = tempPng;
                }
                try
                {
                    var result = await depthEngine!.EstimateDepthAsync(aiInputPath, highQualityVal);
                    var p = ImageProcessor.ProcessForSculptOKQuality(result.Depth, result.Width, result.Height, strengthVal, detailVal, gammaVal, invertVal, highlightsVal, midtonesVal, shadowsVal);
                    currentDepth = p; depthW = result.Width; depthH = result.Height;
                    OutputImage.Source = ImageProcessor.FloatArrayToBitmapSource(p, depthW, depthH);
                }
                finally
                {
                    if (tempPng != null) { try { File.Delete(tempPng); } catch { } }
                }
                GenerateBtn.Content = "DONE"; GenerateBtn.IsEnabled = true;
                SavePngBtn.IsEnabled = true; SaveExrBtn.IsEnabled = true; SaveStlBtn.IsEnabled = true; SaveDdsCompressedBtn.IsEnabled = true; SaveDdsUncompressedBtn.IsEnabled = true;
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); GenerateBtn.Content = "FAILED"; GenerateBtn.IsEnabled = true; }
        }

        private void Mode_Changed(object sender, RoutedEventArgs? e)
        {
            if (AiModelCheck == null) return;
            bool useAi = AiModelCheck.IsChecked == true;
            GenerateBtn.Content = useAi ? "GENERATE AI DEPTH" : "GENERATE RELIEF";
            if (useAi) { InvertCheck.IsChecked = true; HighQualityCheck.IsChecked = true; }
            else { InvertCheck.IsChecked = false; HighQualityCheck.IsChecked = false; DetailSlider.Value = 0.85; ShadowsSlider.Value = 1.2; MidtonesSlider.Value = 1.2; HighlightsSlider.Value = 0.9; }
        }

        private void ReprocessAndShow()
        {
            try
            {
                float detailVal = (float)DetailSlider.Value;
                float gammaVal = (float)GammaSlider.Value;
                float strengthVal = (float)StrengthSlider.Value;
                float highlightsVal = (float)HighlightsSlider.Value;
                float midtonesVal = (float)MidtonesSlider.Value;
                float shadowsVal = (float)ShadowsSlider.Value;
                bool invertVal = InvertCheck.IsChecked == true;
                bool removeBgVal = RemoveBgCheck.IsChecked == true;
                bool useAi = AiModelCheck.IsChecked == true;
                float[] processed;
                if (!useAi && currentBgra != null)
                {
                    processed = ImageProcessor.ProcessTextureAtlas(currentBgra!, depthW, depthH, detailVal, gammaVal, invertVal, highlightsVal, midtonesVal, shadowsVal, removeBgVal);
                    currentDepth = processed;
                }
                else
                {
                    if (currentDepth == null) return;
                    processed = ImageProcessor.ProcessForSculptOKQuality(currentDepth, depthW, depthH, strengthVal, detailVal, gammaVal, invertVal, highlightsVal, midtonesVal, shadowsVal);
                }
                OutputImage.Source = ImageProcessor.FloatArrayToBitmapSource(processed, depthW, depthH);
            }
            catch { }
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (StrengthLabel != null) StrengthLabel.Text = StrengthSlider.Value.ToString("0.0");
            if (DetailLabel != null) DetailLabel.Text = DetailSlider.Value.ToString("0.00");
            if (GammaLabel != null) GammaLabel.Text = GammaSlider.Value.ToString("0.00");
            if (ShadowsLabel != null) ShadowsLabel.Text = ShadowsSlider.Value.ToString("0.00");
            if (MidtonesLabel != null) MidtonesLabel.Text = MidtonesSlider.Value.ToString("0.00");
            if (HighlightsLabel != null) HighlightsLabel.Text = HighlightsSlider.Value.ToString("0.00");
            if (AiModelCheck != null && AiModelCheck.IsChecked == false && currentBgra != null) ReprocessAndShow();
            else if (currentDepth != null) ReprocessAndShow();
        }

        private void SavePng_Click(object sender, RoutedEventArgs e)
        {
            if (currentDepth == null) return;
            var dlg = new SaveFileDialog { Filter = "PNG 16-bit|*.png", FileName = "depth_16bit.png" };
            if (dlg.ShowDialog() == true) ImageProcessor.SaveAs16Bit(currentDepth, depthW, depthH, dlg.FileName);
        }
        private void SaveExr_Click(object sender, RoutedEventArgs e)
        {
            if (currentDepth == null) return;
            var dlg = new SaveFileDialog { Filter = "EXR|*.exr", FileName = "depth_32bit.exr" };
            if (dlg.ShowDialog() == true) ImageProcessor.SaveAsEXR(currentDepth, depthW, depthH, dlg.FileName);
        }
        private void SaveStl_Click(object sender, RoutedEventArgs e)
        {
            if (currentDepth == null) return;
            var dlg = new SaveFileDialog { Filter = "STL|*.stl", FileName = "relief.stl" };
            if (dlg.ShowDialog() == true) StlExporter.SaveAsStl(currentDepth, depthW, depthH, dlg.FileName, 10f);
        }
        private void SaveDdsCompressed_Click(object sender, RoutedEventArgs e)
        {
            if (currentDepth == null) return;
            var dlg = new SaveFileDialog { Filter = "Skyrim Parallax DDS|*.dds", FileName = "texture_p.dds" };
            if (dlg.ShowDialog() == true)
            {
                try { DdsCodec.SaveGrayscaleDxt1(currentDepth, depthW, depthH, dlg.FileName); MessageBox.Show($"Saved DXT1 {dlg.FileName} with {depthW}x{depthH} + mips"); }
                catch (Exception ex) { MessageBox.Show($"DDS save failed: {ex.Message}"); }
            }
        }
        private void SaveDdsUncompressed_Click(object sender, RoutedEventArgs e)
        {
            if (currentDepth == null) return;
            var dlg = new SaveFileDialog { Filter = "Uncompressed DDS|*.dds", FileName = "texture_p.dds" };
            if (dlg.ShowDialog() == true)
            {
                try { DdsCodec.SaveGrayscaleUncompressed(currentDepth, depthW, depthH, dlg.FileName); MessageBox.Show($"Saved Uncompressed {dlg.FileName}"); }
                catch (Exception ex) { MessageBox.Show($"DDS save failed: {ex.Message}"); }
            }
        }
    }
}
