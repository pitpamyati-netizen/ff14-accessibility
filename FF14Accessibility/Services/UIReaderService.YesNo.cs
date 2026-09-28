using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FF14Accessibility.Services;

public sealed partial class UIReaderService
{
    private enum YesNoPress { None, Confirm, Cancel }

    private YesNoPress _yesNoPress;
    private long _yesNoPressAt;
    private bool _yesNoPressReported;
    private string _ynPressedQuestion = string.Empty;
    private bool _holdConfirmActive;
    private long _holdConfirmStart;
    private string _holdConfirmLabel = string.Empty;

    public unsafe void YesNoPressTick()
    {
        if (_yesNoPress == YesNoPress.None) return;
        var ptr = _gameGui.GetAddonByName("SelectYesno");
        if (ptr.IsNull || !((AtkUnitBase*)(nint)ptr)->IsVisible)
        {
            _yesNoPress = YesNoPress.None;
            return;
        }
        if (_yesNoPressReported || Environment.TickCount64 - _yesNoPressAt < 2500) return;
        _yesNoPressReported = true;
        var text = ReadYesNoQuestion((AtkUnitBase*)(nint)ptr);
        if (text == _ynPressedQuestion)
            _tolk.SpeakInterrupt(AccessibilityStrings.YesNoStillOpen);
        _yesNoPress = YesNoPress.None;
    }

    private unsafe AtkResNode* FindVisibleHoldButton(AtkUnitBase* addon, string label)
    {
        if (addon == null || string.IsNullOrWhiteSpace(label)) return null;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || (int)node->Type < 1000 || !node->IsVisible()) continue;
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp == null || !IsReadable(comp) || (int)comp->GetComponentType() != 24) continue;
            if (string.Equals(ReadFirstTextInComponent(node).Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase))
                return node;
        }
        return null;
    }

    private unsafe bool TryStartHeldConfirm(AtkUnitBase* addon)
    {
        var node = FindVisibleHoldButton(addon, _ynConfirmLabel);
        if (node == null) return false;
        DispatchHoldEvent(node, (AtkEventType)6);
        DispatchHoldEvent(node, (AtkEventType)3);
        _holdConfirmActive = true;
        _holdConfirmStart = Environment.TickCount64;
        _holdConfirmLabel = _ynConfirmLabel;
        return true;
    }

    public unsafe void YesNoHoldTick()
    {
        if (!_holdConfirmActive) return;
        var ptr = _gameGui.GetAddonByName("SelectYesno");
        if (ptr.IsNull || !((AtkUnitBase*)(nint)ptr)->IsVisible)
        {
            _holdConfirmActive = false;
            return;
        }
        var addon = (AtkUnitBase*)(nint)ptr;
        var node = FindVisibleHoldButton(addon, _holdConfirmLabel);
        if (node == null) { _holdConfirmActive = false; return; }
        var button = (AtkComponentHoldButton*)((AtkComponentNode*)node)->Component;
        var now = Environment.TickCount64;
        if (!button->IsTargetReached && !button->IsEventFired && now - _holdConfirmStart < 2200) return;
        DispatchHoldEvent(node, (AtkEventType)4);
        _holdConfirmActive = false;
        _yesNoPress = YesNoPress.Confirm;
        _yesNoPressAt = now;
        _yesNoPressReported = false;
        _ynPressedQuestion = ReadYesNoQuestion(addon);
    }

    private unsafe void DispatchHoldEvent(AtkResNode* node, AtkEventType type)
    {
        if (node == null || !IsReadable(node)) return;
        var evt = FindEventOfType(node, type, new List<string>(), 2);
        if (evt == null || evt->Listener == null || !IsReadable(evt->Listener)) return;
        var data = default(AtkEventData);
        evt->Listener->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, &data);
    }
}
