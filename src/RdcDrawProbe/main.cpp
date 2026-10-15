#include <renderdoc_replay.h>
#include <nlohmann/json.hpp>
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <stringise.h>
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

template <> rdcstr DoStringise(const uint32_t &value)
{
  return rdcstr(std::to_string(value).c_str());
}
#include <renderdoc_tostr.inl>
#include <pipestate.inl>

REPLAY_PROGRAM_MARKER()
using json = nlohmann::json;
namespace fs = std::filesystem;

struct GpuValidationLock
{
#ifdef _WIN32
  HANDLE handle = nullptr;
  GpuValidationLock()
  {
    handle = CreateMutexW(nullptr, FALSE, L"Local\\MyHookGpuValidation");
    if(!handle) throw std::runtime_error("Could not create GPU validation lock");
    const DWORD result = WaitForSingleObject(handle, 30000);
    if(result != WAIT_OBJECT_0 && result != WAIT_ABANDONED)
    {
      CloseHandle(handle); handle = nullptr;
      throw std::runtime_error("Another Hook GPU replay or Unity validation is still running");
    }
  }
  ~GpuValidationLock() { if(handle) { ReleaseMutex(handle); CloseHandle(handle); } }
#endif
};

template <class T> std::string text(const T &value)
{
  return std::string(ToStr(value).c_str());
}

std::string text(const ResourceId &value)
{
  uint64_t id = 0;
  static_assert(sizeof(value) == sizeof(id));
  std::memcpy(&id, &value, sizeof(id));
  return "ResourceId::" + std::to_string(id);
}

const ActionDescription *findAction(const rdcarray<ActionDescription> &actions, uint32_t event)
{
  for(const auto &action : actions)
  {
    if(action.eventId == event) return &action;
    if(const auto *child = findAction(action.children, event)) return child;
  }
  return nullptr;
}

void writeBytes(const fs::path &path, const bytebuf &data)
{
  if(data.empty()) throw std::runtime_error("Empty captured buffer: " + path.string());
  std::ofstream file(path, std::ios::binary);
  file.write(reinterpret_cast<const char *>(data.data()), data.size());
  if(!file) throw std::runtime_error("Could not write " + path.string());
}

json buffer(IReplayController *controller, const BoundVBuffer &bound, const fs::path &output,
            const std::string &name)
{
  if(bound.resourceId == ResourceId::Null()) throw std::runtime_error("Missing " + name);
  auto bytes = controller->GetBufferData(bound.resourceId, bound.byteOffset, 0);
  writeBytes(output / (name + ".bin"), bytes);
  return {{"resourceId", text(bound.resourceId)}, {"offset", bound.byteOffset},
          {"stride", bound.byteStride}, {"file", name + ".bin"}, {"size", bytes.size()}};
}

void saveTexture(IReplayController *controller, ResourceId id, const fs::path &file, FileType type)
{
  TextureSave save;
  save.resourceId = id;
  save.destType = type;
  const auto status = controller->SaveTexture(save, rdcstr(file.string().c_str()));
  if(!status.OK()) throw std::runtime_error(status.Message().c_str());
}

int main(int argc, char **argv)
{
  if(argc != 4 && !(argc == 5 && std::string(argv[4]) == "--debug-vertex") &&
     !(argc == 7 && std::string(argv[4]) == "--probe-pixel"))
  {
    std::cerr << "Usage: my-hook-rdc-draw-probe <capture> <EID> <output-directory> [--debug-vertex]\n";
    return 1;
  }
  ICaptureFile *capture = nullptr;
  IReplayController *controller = nullptr;
  bool initialized = false;
  int result = 1;
  std::unique_ptr<GpuValidationLock> gpuLock;
  try
  {
    gpuLock = std::make_unique<GpuValidationLock>();
    const uint32_t event = static_cast<uint32_t>(std::stoul(argv[2]));
    const fs::path output = fs::absolute(argv[3]);
    fs::create_directories(output);
    GlobalEnvironment environment{};
    RENDERDOC_InitialiseReplay(environment, {});
    initialized = true;
    capture = RENDERDOC_OpenCaptureFile();
    auto status = capture->OpenFile(argv[1], "", nullptr);
    if(!status.OK()) throw std::runtime_error(status.Message().c_str());
    auto replay = capture->OpenCapture(ReplayOptions(), nullptr);
    controller = replay.second;
    if(!replay.first.OK() || !controller) throw std::runtime_error(replay.first.Message().c_str());
    if(controller->GetAPIProperties().pipelineType != GraphicsAPI::Vulkan)
      throw std::runtime_error("Current draw probe supports Vulkan captures only");
    const auto *action = findAction(controller->GetRootActions(), event);
    if(!action || !(action->flags & ActionFlags::Drawcall))
      throw std::runtime_error("EID is not a Draw action");
    controller->SetFrameEvent(event, true);
    const auto &pipe = controller->GetPipelineState();
    const auto &vk = controller->GetVulkanPipelineState();
    const auto raster = pipe.GetRasterState();
    const auto depth = pipe.GetDepthTestState();
    json report = {{"schema", "my-hook.raw-draw.v1"}, {"capture", fs::absolute(argv[1]).string()},
                   {"eventId", event}, {"pipelineId", text(pipe.GetGraphicsPipelineObject())},
                   {"topology", text(pipe.GetPrimitiveTopology())},
                   {"indexed", bool(action->flags & ActionFlags::Indexed)},
                   {"numIndices", action->numIndices}, {"numInstances", action->numInstances},
                   {"indexOffset", action->indexOffset}, {"baseVertex", action->baseVertex},
                   {"vertexOffset", action->vertexOffset}, {"instanceOffset", action->instanceOffset},
                   {"primitiveRestart", vk->inputAssembly.primitiveRestartEnable},
                   {"raster", {{"frontCCW", raster.frontCCW}, {"cull", text(raster.cullMode)},
                                {"fill", text(raster.fillMode)},
                                {"depthNegativeOneToOne", vk->viewportScissor.depthNegativeOneToOne},
                                {"depthClamp", vk->rasterizer.depthClampEnable},
                                {"depthClip", vk->rasterizer.depthClipEnable}}},
                   {"depth", {{"enabled", depth.depthEnable}, {"write", depth.depthWrites},
                               {"function", text(depth.depthFunction)}, {"bounds", depth.depthBounds}}},
                   {"stencilEnabled", pipe.IsStencilTestEnabled()}};
    report["viewports"] = json::array();
    for(const auto &view : vk->viewportScissor.viewportScissors)
      report["viewports"].push_back({{"x", view.vp.x}, {"y", view.vp.y},
        {"width", view.vp.width}, {"height", view.vp.height},
        {"minDepth", view.vp.minDepth}, {"maxDepth", view.vp.maxDepth},
        {"scissor", {{"x", view.scissor.x}, {"y", view.scissor.y},
                     {"width", view.scissor.width}, {"height", view.scissor.height},
                     {"enabled", view.scissor.enabled}}}});
    report["blend"] = json::array();
    for(const auto &blend : pipe.GetColorBlends())
      report["blend"].push_back({{"enabled", blend.enabled}, {"logic", blend.logicOperationEnabled},
        {"writeMask", blend.writeMask}, {"colorSource", text(blend.colorBlend.source)},
        {"colorDestination", text(blend.colorBlend.destination)},
        {"colorOperation", text(blend.colorBlend.operation)},
        {"alphaSource", text(blend.alphaBlend.source)},
        {"alphaDestination", text(blend.alphaBlend.destination)},
        {"alphaOperation", text(blend.alphaBlend.operation)}});
    auto stencil = pipe.GetStencilFaces();
    auto stencilJson = [](const StencilFace &face) {
      return json{{"reference", face.reference}, {"readMask", face.compareMask},
        {"writeMask", face.writeMask}, {"function", text(face.function)},
        {"pass", text(face.passOperation)}, {"fail", text(face.failOperation)},
        {"depthFail", text(face.depthFailOperation)}};
    };
    report["stencil"] = {{"front", stencilJson(stencil.first)}, {"back", stencilJson(stencil.second)}};
    report["vertexBuffers"] = json::array();
    auto buffers = pipe.GetVBuffers();
    for(size_t index = 0; index < buffers.size(); ++index)
    {
      if(buffers[index].resourceId == ResourceId::Null())
        report["vertexBuffers"].push_back(nullptr);
      else
        report["vertexBuffers"].push_back(buffer(controller, buffers[index], output,
                                                "vertices" + std::to_string(index)));
    }
    if(action->flags & ActionFlags::Indexed)
      report["indexBuffer"] = buffer(controller, pipe.GetIBuffer(), output, "indices");
    report["attributes"] = json::array();
    for(const auto &attribute : pipe.GetVertexInputs())
      report["attributes"].push_back({{"name", attribute.name.c_str()},
        {"vertexBuffer", attribute.vertexBuffer}, {"offset", attribute.byteOffset},
        {"perInstance", attribute.perInstance}, {"format", attribute.format.Name().c_str()},
        {"used", attribute.used}, {"generic", attribute.genericEnabled}});
    report["samplers"] = json::array();
    report["textures"] = json::array();
    for(auto stage : {ShaderStage::Vertex, ShaderStage::Pixel})
      for(const auto &used : pipe.GetAllUsedDescriptors(false))
      {
        if(used.access.stage != stage ||
           (used.access.type != DescriptorType::Sampler && used.access.type != DescriptorType::ImageSampler))
          continue;
        if(used.access.type == DescriptorType::ImageSampler)
          report["textures"].push_back({{"stage", stage == ShaderStage::Vertex ? "vertex" : "pixel"},
            {"index", used.access.index}, {"resource", text(used.descriptor.resource)},
            {"view", text(used.descriptor.view)}, {"viewFormat", used.descriptor.format.Name().c_str()}});
        const auto &sampler = used.sampler;
        report["samplers"].push_back({{"stage", stage == ShaderStage::Vertex ? "vertex" : "pixel"},
          {"index", used.access.index}, {"resourceId", text(sampler.object)},
          {"u", text(sampler.addressU)}, {"v", text(sampler.addressV)}, {"w", text(sampler.addressW)},
          {"min", text(sampler.filter.minify)}, {"mag", text(sampler.filter.magnify)},
          {"mip", text(sampler.filter.mip)}, {"mipBias", sampler.mipBias},
          {"maxAnisotropy", sampler.maxAnisotropy}, {"minLOD", sampler.minLOD},
          {"maxLOD", sampler.maxLOD}, {"unnormalized", sampler.unnormalized}});
      }
    if(argc == 5)
    {
      auto *trace = controller->DebugVertex(0, 0, action->baseVertex, 0);
      json debug = {{"valid", trace && trace->debugger}, {"eventId", event}};
      if(trace && trace->debugger)
      {
        auto variable = [](const ShaderVariable &value) {
          json entry = {{"name", value.name.c_str()}, {"type", text(value.type)},
                        {"rows", value.rows}, {"columns", value.columns}};
          entry["floatBits"] = json::array();
          for(uint32_t index = 0; index < value.rows * value.columns; ++index)
            entry["floatBits"].push_back(value.value.u32v[index]);
          return entry;
        };
        debug["inputs"] = json::array();
        for(const auto &value : trace->inputs) debug["inputs"].push_back(variable(value));
        debug["changes"] = json::array();
        uint32_t steps = 0;
        while(steps < 10000)
        {
          auto states = controller->ContinueDebug(trace->debugger);
          if(states.empty()) break;
          steps += uint32_t(states.size());
          for(const auto &state : states)
            for(const auto &change : state.changes)
              debug["changes"].push_back({{"step", state.stepIndex}, {"after", variable(change.after)}});
        }
        debug["steps"] = steps;
      }
      if(trace) controller->FreeTrace(trace);
      std::ofstream file(output / "vertex-debug.json");
      file << debug.dump(2);
    }
    const auto targets = pipe.GetOutputTargets();
    if(targets.empty() || targets[0].resource == ResourceId::Null())
      throw std::runtime_error("Draw has no color attachment");
    const ResourceId color = targets[0].resource;
    report["colorTarget"] = text(color);
    for(const auto &texture : controller->GetTextures())
      if(texture.resourceId == color) report["colorTargetFormat"] = texture.format.Name().c_str();
    controller->SetFrameEvent(event, true);
    saveTexture(controller, color, output / "after.exr", FileType::EXR);
    saveTexture(controller, color, output / "after.png", FileType::PNG);
    if(argc == 7)
    {
      const uint32_t x = uint32_t(std::stoul(argv[5]));
      const uint32_t y = uint32_t(std::stoul(argv[6]));
      json history = {{"eventId", event}, {"x", x}, {"y", y}};
      auto pick = [&](uint32_t row) {
        const auto value = controller->PickPixel(color, x, row, Subresource(), CompType::Typeless);
        return json::array({value.floatValue[0], value.floatValue[1], value.floatValue[2], value.floatValue[3]});
      };
      history["pickAfter"] = pick(y);
      history["pickAfterMirroredY"] = pick(uint32_t(vk->viewportScissor.viewportScissors[0].vp.height) - 1 - y);
      controller->SetFrameEvent(event - 1, true);
      history["pickBefore"] = pick(y);
      history["pickBeforeMirroredY"] = pick(uint32_t(vk->viewportScissor.viewportScissors[0].vp.height) - 1 - y);
      controller->SetFrameEvent(event, true);
      std::ofstream file(output / "pixel-values.json");
      file << history.dump(2);
    }
    controller->SetFrameEvent(event - 1, true);
    saveTexture(controller, color, output / "before.exr", FileType::EXR);
    saveTexture(controller, color, output / "before.png", FileType::PNG);
    const auto depthTarget = controller->GetPipelineState().GetDepthTarget();
    if(depthTarget.resource != ResourceId::Null())
      saveTexture(controller, depthTarget.resource, output / "before-depth.exr", FileType::EXR);
    std::ofstream file(output / "raw-draw.json");
    file << report.dump(2);
    if(!file) throw std::runtime_error("Could not write raw-draw.json");
    std::cout << "Exported exact draw inputs and state at EID " << event << "\n";
    result = 0;
  }
  catch(const std::exception &error) { std::cerr << error.what() << "\n"; }
  if(controller) controller->Shutdown();
  if(capture) capture->Shutdown();
  if(initialized) RENDERDOC_ShutdownReplay();
  return result;
}
