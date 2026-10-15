using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DiffusionButton : UdonSharpBehaviour {
    public DiffusionTeaching teaching;
    public DiffusionController controller;
    public DiffusionLocalization localization;
    public string command;
    public int value;
    public Button ui;
    public CanvasGroup appearance;
    public void Refresh(){bool allowed=controller==null||controller.CanUseCommand(command);if(ui!=null){ui.interactable=allowed;if(ui.targetGraphic!=null)ui.targetGraphic.color=allowed?new Color(.07f,.19f,.26f,1):new Color(.12f,.12f,.12f,.45f);}if(appearance!=null)appearance.alpha=allowed?1:.32f;}
    public void Press() {
        if(teaching!=null) {teaching.commandValue=value;teaching.SendCustomEvent(command);return;}
        if(localization!=null) {localization.languageOverride=value;localization.SendCustomEvent("SetLanguage");return;}
        if(controller==null||!controller.CanUseCommand(command)) return;
        controller.commandValue=value;
        controller.SendCustomEvent(command);
    }
    public override void Interact() { Press(); }
}
