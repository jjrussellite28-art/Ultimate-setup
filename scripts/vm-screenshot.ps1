<#
Saves what the Hyper-V test VM shows on its console as PNG. Works at the sign-in screen too.
    scripts\vm-screenshot.ps1 -Path shot.png
#>
param(
    [string]$VMName = 'Win11-Test',
    [Parameter(Mandatory)][string]$Path
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$ns = 'root\virtualization\v2'
$vm = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem -Filter "ElementName='$VMName'"
$head = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_VideoHead | Select-Object -First 1
$width = [int]$head.CurrentHorizontalResolution
$height = [int]$head.CurrentVerticalResolution
$settings = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_VirtualSystemSettingData |
    Where-Object VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized'
$mgmt = Get-CimInstance -Namespace $ns -ClassName Msvm_VirtualSystemManagementService
$result = Invoke-CimMethod -InputObject $mgmt -MethodName GetVirtualSystemThumbnailImage -Arguments @{
    TargetSystem = $settings; WidthPixels = [uint16]$width; HeightPixels = [uint16]$height }
if ($result.ReturnValue -ne 0) { throw "GetVirtualSystemThumbnailImage failed: $($result.ReturnValue)" }

# The image comes as RGB565, which Format16bppRgb565 takes as is.
$bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format16bppRgb565)
$rect = [System.Drawing.Rectangle]::new(0, 0, $width, $height)
$data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bitmap.PixelFormat)
try {
    $bytes = [byte[]]$result.ImageData
    for ($y = 0; $y -lt $height; $y++) {
        [Runtime.InteropServices.Marshal]::Copy($bytes, $y * $width * 2, [IntPtr]::Add($data.Scan0, $y * $data.Stride), $width * 2)
    }
}
finally { $bitmap.UnlockBits($data) }
$bitmap.Save([IO.Path]::GetFullPath($Path), [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
"$Path ($width x $height)"
