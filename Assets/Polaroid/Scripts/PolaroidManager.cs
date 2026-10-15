
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace Fairplex.Polaroid
{
    public class PolaroidManager : UdonSharpBehaviour
    {
        public Camera cameraPhoto;
        public AudioSource audioSource;
        public Animator polaroidAnimator;
        public Transform photoPlaceholder;
        public RenderTexture lens;
        public Text remainText;
        private GameObject currentPhoto;
        public GameObject screen;
        public GameObject[] photo;
        public Material lensMaterial;
        public Material[] photoMaterial;
        public RenderTexture[] renderTexture;
        [UdonSynced] public int photoIndex = 0;
        [UdonSynced] public bool isBusy = false;
        [UdonSynced] public int cameraState = 0;
        [UdonSynced] public bool PhotoState = false;
        [UdonSynced] public bool sleeping = false;


#if UNITY_EDITOR
        private void Start()
        {
            foreach (RenderTexture rt in renderTexture)
            {
                rt.Release();
            }
        }
#endif

        private void LateUpdate()
        {
            if (isBusy && currentPhoto != null)
            {
                currentPhoto.transform.SetPositionAndRotation(photoPlaceholder.position, photoPlaceholder.rotation);
            }
        }
        public void SetPhotoIndex()
        {
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }

            if (photoIndex < 10)
            {
                photoIndex++;
                cameraState = 2;

                SetText();
                RequestSerialization();
                TakePhoto(photoIndex - 1);
            }
        }

        public void OnCameraPickup()
        {
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }

            cameraState = 1;
            cameraPhoto.enabled = true;
            RequestSerialization();
        }

        public void OnCameraDrop()
        {
            cameraState = 3;
            cameraPhoto.enabled = false;
            RequestSerialization();
        }

        public void TakePhoto(int idx)
        {
            if (idx >= 10) return;

            isBusy = true;

            currentPhoto = photo[idx];
            cameraPhoto.enabled = true;
            cameraPhoto.targetTexture = renderTexture[idx];
            currentPhoto.transform.Find("Photo").gameObject.GetComponent<Renderer>().material = photoMaterial[idx];

            lensMaterial.SetColor("_EmissionColor", Color.black);

            audioSource.pitch = Random.Range(0.98f, 1.02f);
            audioSource.Play();

            polaroidAnimator.SetTrigger("TakePhoto");
            polaroidAnimator.SetTrigger("DevelopPhoto " + idx);

            SendCustomEventDelayedSeconds(nameof(_SnapDone), 0.25f);
            SendCustomEventDelayedSeconds(nameof(_Done), 2f);

            SetText();
        }

        public void _SnapDone()
        {
            if (!Networking.IsOwner(gameObject))
            {
                cameraPhoto.enabled = false;
            }

            currentPhoto.SetActive(true);

            cameraPhoto.targetTexture = lens;
            lensMaterial.SetColor("_EmissionColor", Color.white);
        }

        public void _Done()
        {
            currentPhoto.GetComponent<Collider>().enabled = true;
            isBusy = false;
        }
        public void _Reset()
        {
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }

            foreach (GameObject pics in photo)
            {
                pics.SetActive(false);
                pics.GetComponent<Collider>().enabled = false;
            }
            foreach (RenderTexture rt in renderTexture)
            {
                rt.Release();
            }

            cameraState = 4;
            photoIndex = 0;
            polaroidAnimator.SetTrigger("Reset");

            SetText();

            RequestSerialization();
        }

        public void SetText()
        {
            remainText.text = (photo.Length - photoIndex).ToString();
        }

        public override void OnDeserialization()
        {
            if (cameraState == 1) cameraPhoto.enabled = true;

            else if (cameraState == 2) TakePhoto(photoIndex - 1);

            else if (cameraState == 3) cameraPhoto.enabled = false;

            else if (cameraState == 4)
            {
                foreach (GameObject pics in photo)
                {
                    pics.SetActive(false);
                    pics.GetComponent<Collider>().enabled = false;
                }
                foreach (RenderTexture rt in renderTexture)
                {
                    rt.Release();
                }
                photoIndex = 0;
                polaroidAnimator.SetTrigger("Reset");
            }

            SetText();
        }
    }
}