// Spike S1: is there a usable Metal device on this machine? Run with: swift metal_probe.swift
import Foundation
import Metal

let all = MTLCopyAllDevices()
print("MTLCopyAllDevices: \(all.count) device(s)")
guard let dev = MTLCreateSystemDefaultDevice() else {
    print("MTLCreateSystemDefaultDevice: nil (no Metal device)")
    exit(1)
}
print("name: \(dev.name)")
print("registryID: \(dev.registryID)")
print("isLowPower: \(dev.isLowPower) isHeadless: \(dev.isHeadless) isRemovable: \(dev.isRemovable)")
print("hasUnifiedMemory: \(dev.hasUnifiedMemory)")
print("recommendedMaxWorkingSetSize: \(dev.recommendedMaxWorkingSetSize / 1_048_576) MiB")
print("maxBufferLength: \(dev.maxBufferLength / 1_048_576) MiB")
let families: [(String, MTLGPUFamily)] = [
    ("apple7", .apple7), ("apple8", .apple8), ("apple9", .apple9),
    ("mac2", .mac2), ("metal3", .metal3),
]
for (n, f) in families { print("supportsFamily(\(n)): \(dev.supportsFamily(f))") }
print("argumentBuffersSupport tier: \(dev.argumentBuffersSupport.rawValue)")

// Prove it executes work: clear a 4x4 texture on the GPU and read it back.
guard let queue = dev.makeCommandQueue() else { print("makeCommandQueue failed"); exit(1) }
let desc = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba8Unorm, width: 4, height: 4, mipmapped: false)
desc.usage = [.renderTarget]
desc.storageMode = .shared
guard let tex = dev.makeTexture(descriptor: desc) else { print("makeTexture failed"); exit(1) }
let rp = MTLRenderPassDescriptor()
rp.colorAttachments[0].texture = tex
rp.colorAttachments[0].loadAction = .clear
rp.colorAttachments[0].storeAction = .store
rp.colorAttachments[0].clearColor = MTLClearColor(red: 0, green: 1, blue: 0, alpha: 1)
let cb = queue.makeCommandBuffer()!
cb.makeRenderCommandEncoder(descriptor: rp)!.endEncoding()
cb.commit()
cb.waitUntilCompleted()
if let err = cb.error { print("command buffer error: \(err)"); exit(1) }
var px = [UInt8](repeating: 0, count: 4 * 4 * 4)
tex.getBytes(&px, bytesPerRow: 16, from: MTLRegionMake2D(0, 0, 4, 4), mipmapLevel: 0)
print("Metal clear readback pixel0: \(px[0..<4].map { String($0) }.joined(separator: ","))")
if px[0] != 0 || px[1] != 255 || px[3] != 255 { print("FAIL: unexpected pixel"); exit(1) }
print("Metal GPU clear: OK")
