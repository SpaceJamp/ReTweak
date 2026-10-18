"""Find known NVAPI QueryInterface IDs embedded as imm32 in a binary."""
import struct, sys

IDS = {
    0x0150E828: "NvAPI_Initialize",
    0xD22BDD7E: "NvAPI_Unload",
    0xE5AC921F: "NvAPI_EnumPhysicalGPUs",
    0x1BE0B8E5: "NvAPI_GPU_GetBusId",
    0x2A0A350F: "NvAPI_GPU_GetBusSlotId",
    0x2DDFB66E: "NvAPI_GPU_GetPCIIdentifiers",
    0xCEEE8E9F: "NvAPI_GPU_GetFullName",
    0x4D7B0709: "NvAPI_I2CReadEx",
    0x283AC65A: "NvAPI_I2CWriteEx",
    0x2FDE12C5: "NvAPI_I2CRead",
    0xE812EB07: "NvAPI_I2CWrite",
    0x6FF81213: "NvAPI_GPU_GetPstates20",
    0xE3640A56: "NvAPI_GPU_GetThermalSettings",
    0x5F608315: "NvAPI_GPU_GetTachReading",
}

d = open(sys.argv[1], "rb").read()
for i in range(len(d) - 4):
    v = struct.unpack_from("<I", d, i)[0]
    if v in IDS:
        ctx = d[max(0, i - 6):i + 10].hex(" ")
        print(f"{i:08x}  {IDS[v]:32s} prev/next bytes: {ctx}")
