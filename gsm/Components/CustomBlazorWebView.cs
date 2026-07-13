using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.FileProviders;
using System;
using System.IO;

namespace gsm.Components
{
    public class CustomBlazorWebView : BlazorWebView
    {
        public override IFileProvider CreateFileProvider(string contentRootDir)
        {
            // Trong môi trường Single-File Publish, Assembly.Location sẽ rỗng,
            // dẫn đến BlazorWebView tìm sai thư mục.
            // Override này bắt buộc nó tìm contentRootDir trong AppContext.BaseDirectory.
            var baseDir = AppContext.BaseDirectory;
            var fullPath = Path.Combine(baseDir, contentRootDir);
            
            // Đảm bảo thư mục tồn tại để không bị crash khi PhysicalFileProvider khởi tạo
            if (!Directory.Exists(fullPath))
            {
                try { Directory.CreateDirectory(fullPath); } catch {}
            }

            return new PhysicalFileProvider(fullPath);
        }
    }
}
