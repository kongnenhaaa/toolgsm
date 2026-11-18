THIẾT LẬP ĐỒNG BỘ GOOGLE SHEETS CHO TOOLGSM
================================================

Chỉ cần triển khai Apps Script một lần cho một tài khoản Google. Gateway này có
thể ghi vào nhiều Google Sheet mà tài khoản đó có quyền chỉnh sửa.

1. Mở https://script.google.com/ và chọn New project.
2. Mở file Code.gs trong thư mục này, copy toàn bộ mã vào project.
3. Trong ToolGSM > Cài đặt > Google Sheets, nhấn "Thêm link Google Sheet".
   ToolGSM sẽ tự sinh "Mã bảo mật đồng bộ".
4. Thay PASTE_THE_SAME_SECRET_FROM_TOOLGSM_HERE trong Code.gs bằng đúng mã đó.
   Giữ nguyên hai dấu nháy đơn bao quanh mã.
5. Trong Apps Script chọn Deploy > New deployment > Web app:
   - Execute as: Me
   - Who has access: Anyone
   Sau đó bấm Deploy và cấp quyền truy cập Google Sheets.
6. Copy Web app URL kết thúc bằng /exec vào ô "Apps Script Web App URL".
7. Thêm từng link Google Sheet, đặt tên làm việc và tên tab muốn ghi, rồi Lưu.
8. Tại bảng COM, bấm "Đồng bộ Google Sheet", chọn link và bấm Đồng bộ.

LƯU Ý AN TOÀN
- Không chia sẻ Web app URL và mã bảo mật.
- ToolGSM gửi dữ liệu thẳng tới Apps Script của tài khoản Google; không đi qua
  server trung gian khác.
- Mỗi lần đồng bộ sẽ thay toàn bộ nội dung của tab đã cấu hình. Các tab khác
  trong cùng file Google Sheet không bị thay đổi.
- Nếu sửa Code.gs, vào Deploy > Manage deployments > Edit > New version để
  bản đang chạy nhận mã mới.
