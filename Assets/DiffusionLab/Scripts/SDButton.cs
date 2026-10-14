using UdonSharp;
using UnityEngine;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SDButton : UdonSharpBehaviour {
    public SDController controller;
    public string command;
    public int value;
    public void Press(){if(controller==null)return;controller.commandValue=value;controller.SendCustomEvent(command);}
    public override void Interact(){Press();}
}
