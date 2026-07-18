using System;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Windows.Input;
using AnvilDepth.Services;

namespace AnvilDepth
{
    public partial class MainWindow : Window
    {
        private string? currentImagePath;
        private float[]? currentDepth;
        private int depthW, depthH;
        private DepthEngine? depthEngine;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += async (s,e) => {
                depthEngine = new DepthEngine();
                var gpuInfo = await depthEngine.InitializeAsync();
                GpuStatusText.Text = gpuInfo;
            };
        }
        private void DropZone_DragOver(object sender, DragEventArgs e){ e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
        private void DropZone_Drop(object sender, DragEventArgs e){ if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) LoadImage(files[0]); }
        private void DropZone_MouseDown(object sender, MouseButtonEventArgs e){ var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tiff" }; if (dlg.ShowDialog() == true) LoadImage(dlg.FileName); }
        private void LoadImage(string path){ currentImagePath = path; var bmp = new BitmapImage(new Uri(path)); PreviewImage.Source = bmp; PreviewImage.Visibility = Visibility.Visible; DropText.Visibility = Visibility.Collapsed; GenerateBtn.IsEnabled = true; }
        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e){ if (StrengthLabel != null) StrengthLabel.Text = StrengthSlider.Value.ToString("0.0"); if (DetailLabel != null) DetailLabel.Text = DetailSlider.Value.ToString("0.00"); if (GammaLabel != null) GammaLabel.Text = GammaSlider.Value.ToString("0.0"); if (currentDepth != null) ReprocessAndShow(); }
        private async void GenerateBtn_Click(object sender, RoutedEventArgs e){
            if (currentImagePath == null || depthEngine == null) return;
            GenerateBtn.IsEnabled = false; GenerateBtn.Content = "RUNNING ON RTX...";
            try{
                var result = await depthEngine.GenerateDepthAsync(currentImagePath, RemoveBgCheck.IsChecked == true, HighQualityCheck.IsChecked == true);
                currentDepth = result.Depth; depthW = result.Width; depthH = result.Height;
                ReprocessAndShow(); ExportPanel.Visibility = Visibility.Visible;
            }catch(Exception ex){ MessageBox.Show($"GPU Error: {ex.Message}"); }
            finally{ GenerateBtn.IsEnabled = true; GenerateBtn.Content = "GENERATE DEPTH MAP (RTX)"; }
        }
        private void ReprocessAndShow(){
            if (currentDepth == null) return;
            var processed = ImageProcessor.ProcessForSculptOKQuality(currentDepth, depthW, depthH, (float)StrengthSlider.Value, (float)DetailSlider.Value, (float)GammaSlider.Value, InvertCheck.IsChecked == true);
            DepthPreview.Source = ImageProcessor.FloatArrayToBitmapSource(processed, depthW, depthH);
            DepthPreview.Visibility = Visibility.Visible; PlaceholderText.Visibility = Visibility.Collapsed;
            var variants = new float[] { 0.8f, 1.0f, 1.5f, 2.2f };
            var images = new[] { Var1, Var2, Var3, Var4 };
            for (int i = 0; i < 4; i++){ var v = ImageProcessor.ApplyGamma(processed, variants[i]); images[i].Source = ImageProcessor.FloatArrayToBitmapSource(v, depthW, depthH); }
        }
        private void Tab_Checked(object sender, RoutedEventArgs e){ if (DepthPreview == null) return; DepthPreview.Visibility = DepthTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; VariantsGrid.Visibility = VariantsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
        private void Save16Bit_Click(object sender, RoutedEventArgs e){ if (currentDepth == null) return; var dlg = new SaveFileDialog { Filter = "16-bit PNG|*.png", FileName = "depth_16bit.png" }; if (dlg.ShowDialog() == true){ var p = ImageProcessor.ProcessForSculptOKQuality(currentDepth, depthW, depthH, (float)StrengthSlider.Value, (float)DetailSlider.Value, (float)GammaSlider.Value, InvertCheck.IsChecked == true); ImageProcessor.SaveAs16Bit(p, depthW, depthH, dlg.FileName); MessageBox.Show($"Saved {dlg.FileName}"); } }
        private void SaveEXR_Click(object sender, RoutedEventArgs e){ var dlg = new SaveFileDialog { Filter = "EXR|*.exr", FileName = "depth_32bit.exr" }; if (dlg.ShowDialog() == true){ var p = ImageProcessor.ProcessForSculptOKQuality(currentDepth!, depthW, depthH, (float)StrengthSlider.Value, (float)DetailSlider.Value, (float)GammaSlider.Value, InvertCheck.IsChecked == true); ImageProcessor.SaveAsEXR(p, depthW, depthH, dlg.FileName); MessageBox.Show($"Saved {dlg.FileName}"); } }
        private void SaveSTL_Click(object sender, RoutedEventArgs e){ var dlg = new SaveFileDialog { Filter = "STL|*.stl", FileName = "relief.stl" }; if (dlg.ShowDialog() == true){ var p = ImageProcessor.ProcessForSculptOKQuality(currentDepth!, depthW, depthH, (float)StrengthSlider.Value, (float)DetailSlider.Value, (float)GammaSlider.Value, InvertCheck.IsChecked == true); StlExporter.SaveStl(p, depthW, depthH, dlg.FileName, 10); MessageBox.Show($"Saved {dlg.FileName}"); } }
    }
}
