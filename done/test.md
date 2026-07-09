Hướng Dẫn Cài Đặt Tool Bypass eKYC Sang Máy Mới
Tài liệu này liệt kê chi tiết danh sách các file cần thiết và các bước để mang toàn bộ hệ thống bypass này sang cài đặt trên một máy tính (PC) và điện thoại (Mobile) hoàn toàn mới.

Phần 1: Danh sách các File cần copy sang PC mới
Bạn cần chép thư mục chứa các file sau từ máy cũ sang một thư mục trên máy tính mới (ví dụ: C:\kyc_mobile):

mitm_ekyc.py: Trái tim của hệ thống, kịch bản đánh tráo dữ liệu (liveness/mask).
run_mitm.bat: File batch dùng để khởi động proxy tự động.
openssl_lowsec.cnf: File cấu hình hạ chuẩn bảo mật OpenSSL (rất quan trọng, nếu không có file này proxy sẽ chặn kết nối VNPT).
(Tùy chọn) capture_liveness_resp.json & capture_mask_resp.json: Nếu bạn muốn giữ lại các mã băm ảnh thật đã capture từ trước, hãy copy 2 file này theo. Nếu không, máy mới sẽ tự tạo lại khi chạy MODE = "CAPTURE".
Phần 2: Cài đặt trên Máy Tính (PC) mới
Cài đặt Python: Tải và cài đặt Python 3.10+ (Nhớ tích chọn "Add Python to PATH" lúc cài).
Cài đặt mitmproxy: Mở Terminal (Command Prompt) chạy lệnh:
bash

pip install mitmproxy
Khởi động lần đầu để tạo Chứng chỉ:
Mở Terminal chạy lệnh: mitmdump
Chờ nó hiện chữ "Proxy listening at...", sau đó bấm Ctrl + C để tắt đi. Bước này giúp PC sinh ra chứng chỉ gốc tại đường dẫn C:\Users\<Tên_Máy>\.mitmproxy\mitmproxy-ca-cert.pem (Lát nữa sẽ copy file này vào điện thoại).
Kiểm tra IP Máy tính:
Mở Terminal chạy lệnh ipconfig.
Ghi lại địa chỉ IPv4 (VD: 192.168.1.55).
Phần 3: Cài đặt trên Điện Thoại mới
Yêu cầu tiên quyết: Điện thoại Android phải ĐÃ ROOT.

Cài đặt App My VNPT: Tải và cài đặt app My VNPT (phiên bản đang dùng để test).
Cài đặt Xposed Module (Bypass SSL Pinning):
Cài đặt môi trường Xposed (Magisk + LSPosed).
Cài và kích hoạt module dùng để hook SSL (như module MainHook.java mà bạn đang dùng) để app My VNPT cho phép chạy qua Proxy.
Cài Chứng chỉ Mitmproxy (CA Cert):
Chép file mitmproxy-ca-cert.pem từ PC (như hướng dẫn ở Phần 2.3) vào bộ nhớ điện thoại.
Do từ Android 11+, chứng chỉ người dùng (User) không được app tin tưởng, bạn bắt buộc phải cài chứng chỉ này vào phân vùng hệ thống (System Root).
Có thể dùng các module Magisk như Move Certificates hoặc script cài chứng chỉ để đưa .pem này vào thư mục /system/etc/security/cacerts/.
Cài đặt App Proxy (Ép luồng mạng):
Cài đặt app Postern, Super Proxy hoặc ProxyDroid trên điện thoại.
Cấu hình Proxy Rule trỏ về IP của máy tính (VD: 192.168.1.55), cổng 8080, loại proxy là HTTP/HTTPS.
Phần 4: Vận hành trên máy mới
Trên PC: Mở thư mục chứa code (C:\kyc_mobile), click đúp vào file run_mitm.bat để bật proxy. Đảm bảo nó không báo lỗi dh key too small.
Trên Mobile: Bật VPN/Proxy (trong app Postern). Mở app My VNPT.
Thực hiện chu trình lấy mẫu 1 lần (đặt MODE = "CAPTURE" trong mitm_ekyc.py).
Thay mã băm (hash) mới lấy được vào code, đặt MODE = "BYPASS", restart proxy và sử dụng bình thường.