using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DiffusionButton : UdonSharpBehaviour {
    public DiffusionController controller;
    public string command;
    public int value;
    public override void Interact() {
        if(controller==null) return;
        controller.commandValue=value;
        controller.SendCustomEvent(command);
    }
}
