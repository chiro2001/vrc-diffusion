using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SDButton : UdonSharpBehaviour {
    public SDController controller;
    public string command;
    public int value;
    public Button ui;
    public CanvasGroup appearance;
    public void Refresh(){bool allowed=controller!=null&&controller.CanCommand(command);if(ui!=null){ui.interactable=allowed;if(ui.targetGraphic!=null)ui.targetGraphic.color=allowed?new Color(.07f,.19f,.26f,1):new Color(.12f,.12f,.12f,.45f);}if(appearance!=null)appearance.alpha=allowed?1:.32f;}
    public void Press(){if(controller==null||!controller.CanCommand(command))return;controller.commandValue=value;controller.SendCustomEvent(command);Refresh();}
    public override void Interact(){Press();}
}
