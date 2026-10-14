using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DiffusionButton : UdonSharpBehaviour {
    public DiffusionTeaching teaching;
    public DiffusionController controller;
    public DiffusionLocalization localization;
    public string command;
    public int value;
    public void Press() {
        if(teaching!=null) {teaching.commandValue=value;teaching.SendCustomEvent(command);return;}
        if(localization!=null) {localization.languageOverride=value;localization.SendCustomEvent("SetLanguage");return;}
        if(controller==null) return;
        controller.commandValue=value;
        controller.SendCustomEvent(command);
    }
    public override void Interact() { Press(); }
}
