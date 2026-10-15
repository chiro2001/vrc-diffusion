
using UdonSharp;
using UnityEngine;

namespace Fairplex.Polaroid
{
    public class PolaroidPickup : UdonSharpBehaviour
    {
        public PolaroidManager polaroidManager;
        public Camera cameraPhoto;

        public override void OnPickup()
        {
            polaroidManager.OnCameraPickup();
        }

        public override void OnDrop()
        {
            polaroidManager.OnCameraDrop();
        }
        public override void OnPickupUseDown()
        {
            if (!polaroidManager.isBusy)
            {
                polaroidManager.SetPhotoIndex();
            }
        }
    }
}