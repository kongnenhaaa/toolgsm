# Phân tích 5 minidump ngày 27–29/10/2026

Nguồn: `C:\Users\congn\Downloads\Minidump\Minidump\*.dmp`. Người dùng xác nhận cả 5 dump thuộc **một máy**. Đã đọc bằng WinDbg/CDB 10.0.29661.1004 với Microsoft symbols. Thời gian trong bảng là UTC+7 do chính debugger ghi. Đây là mini kernel dump, chỉ lưu một phần bộ nhớ và stack; không chứa lịch sử lệnh AT hay đủ dữ liệu để chứng minh chuỗi nhân quả tuyệt đối.

| Thời điểm | File | Bugcheck | Tiến trình tại thời điểm lỗi | Bằng chứng chính |
|---|---|---|---|---|
| 27/10 19:05:50 | `102726-12750-01.dmp` | `0x13A`, heap corruption, subtype `0x11` | `System` | Phát hiện heap kernel hỏng lúc hệ thống bảo trì heap; stack không chỉ ra USB hay ToolGSM. Không thể gán lỗi này cho tool từ dump. |
| 28/10 15:55:15 | `102826-7671-01.dmp` | `0xA`, IRQL_NOT_LESS_OR_EQUAL | `ToolGSM.exe` | Failure bucket `AV_xrusbser!unknown_function`; stack `xrusbser+0xd09b` → `nt!KeSetEvent`, truy cập địa chỉ 0 tại IRQL 2. |
| 28/10 17:30:09 | `102826-9375-01.dmp` | `0x50`, PAGE_FAULT_IN_NONPAGED_AREA | `ToolGSM.exe` | Cùng failure hash với lỗi 0xA; stack `xrusbser+0xcea4` → `usbccgp` → `UsbHub3` → `ACPI`, địa chỉ bộ nhớ không hợp lệ. |
| 28/10 23:46:50 | `102826-11125-01.dmp` | `0x44`, MULTIPLE_IRP_COMPLETE_REQUESTS | `ToolGSM.exe` | Stack `xrusbser+0xcea4` → `usbccgp` → `UsbHub3` → `ACPI`; `!irp` cho thấy IRP đã hoàn tất, có frame `IRP_MJ_WRITE`, và completion routine của `xrusbser`. Bucket tự động ghi `ACPI`, nhưng nó không chứng minh ACPI là bên hoàn tất IRP lần đầu. |
| 29/10 14:26:06 | `102926-7500-01.dmp` | `0xD1`, DRIVER_IRQL_NOT_LESS_OR_EQUAL | `ToolGSM.exe` | Fault tại `USBXHCI!Bulk_TransferData_Initialize+0x89` trong đường USB bulk transfer; `hcmon.sys` nằm trên stack, có thể là filter USB bổ sung. |

`xrusbser.sys` trong dump có PE timestamp 05/01/2023. Dump không chứa đủ metadata để khẳng định số phiên bản driver đang cài. Nhà sản xuất MaxLinear liệt kê driver Windows 10 trở lên phiên bản 2.7.0.0, phát hành 01/2023, cho XR21V1414 và các USB UART liên quan. Vì vậy không nên mặc định rằng chỉ cần tìm bản driver mới hơn là giải quyết được.

## Kết luận kỹ thuật

* Bốn dump xảy ra khi tiến trình `ToolGSM.exe` là process context và đều có đường USB/driver liên quan. Hai dump có `xrusbser` là failure bucket. Điều này là bằng chứng mạnh rằng hoạt động I/O COM của tool kích hoạt sự cố trong stack driver USB UART trên hệ thống được thu dump.
* Dump `0x44` chứng minh lỗi hoàn tất IRP lặp ở kernel. Ứng dụng .NET ở user mode không trực tiếp gọi `IoCompleteRequest`; điểm hỏng nằm ở driver stack. Tool vẫn có trách nhiệm giảm các mẫu I/O nguy hiểm mà nó tạo ra.
* Dump `0x13A` không đủ thông tin để quy cho tool. Cả 5 dump chỉ mô tả **một máy**; chưa có dữ liệu crash của hai máy còn lại hoặc bản `ToolGSM.exe` nào đã được phát cho họ.

## Rủi ro trong source và phần đã sửa

* `GsmModemService.Disconnect()` trước đây xóa lượt reboot tự động khi COM rớt. Modem reboot có thể tự làm COM biến mất rồi xuất hiện lại, khiến cùng cổng được cấp thêm lượt reboot. Đã giữ lượt reboot qua reconnect trong cùng phiên chạy.
* `WriteSautoCommandWhileLockedAsync()` trước đây trả timeout sau ba giây rồi nhả khóa COM, dù `SerialPort.WriteLine` còn có thể chạy tới `WriteTimeout` 20 giây. Đã giữ khóa đến khi write hoàn tất.
* Các đường `AT+CFUN=1,1` đã được đưa qua `ModemRebootPacer` để cách nhau ít nhất 10 giây giữa các lần gửi. Những thay đổi source này **chưa chứng minh** đã có trong bản EXE gây crash và **chưa được xác nhận** trên bank modem thật.
* `HandleDataReceivedCore()` còn gọi `Disconnect()`/`SerialPort.Close()` ngay trong callback nhận dữ liệu khi gặp `IOException` hoặc `UnauthorizedAccessException`. Đây là một rủi ro race với I/O đang chạy, nhưng mini dump không chứng minh đây là đường trực tiếp gây ra năm bugcheck. Cần thiết kế lại đóng cổng theo vòng đời I/O và thử trên driver/hardware thật trước khi khẳng định đã xử lý dứt điểm.

Toàn bộ 920 test hiện tại chạy đạt. Test không thể kiểm chứng lỗi kernel `xrusbser.sys` trên máy khách.

## Bước xác minh trên máy bị lỗi

1. Trên máy đã có dump, đối chiếu số COM/modem, sơ đồ hub, thời điểm thao tác và hash/phiên bản `ToolGSM.exe` đã chạy. Thu dump hoặc Event Viewer riêng cho hai máy còn lại trước khi kết luận chúng cùng lỗi.
2. Thu Event Viewer và phiên bản `xrusbser`/USB host controller bằng `tools/CollectResetDiagnostics.ps1` tại giờ crash tương ứng.
3. Tạm ngưng chạy bản EXE đã gây màn hình xanh trên bank USB đó. Kiểm tra driver với bản chính thức của MaxLinear và firmware/USB hub; nếu đã là 2.7.0.0, gửi dump cho nhà sản xuất driver và thử cô lập bank trên controller/hub khác dưới giám sát.
4. Chỉ đánh giá bản tool đã sửa sau khi build đúng source mới và thử có kiểm soát từng bank; không suy từ unit test rằng lỗi BSOD đã hết.

Tài liệu: [Microsoft 0x44](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-0x44--multiple-irp-complete-requests), [Microsoft 0x13A](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-0x13a--kernel-mode-heap-corruption), [MaxLinear drivers](https://www.maxlinear.com/support/design-tools/software-drivers), [MaxLinear AN213](https://test.maxlinear.com/appnote/an213.pdf).
