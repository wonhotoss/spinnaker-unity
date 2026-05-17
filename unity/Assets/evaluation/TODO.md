## 카메라 평가 뷰
- 기존 구현을 평가해서 spinnaker 카메라 영상을 평가하는 뷰. 과노출, 저노출 영역을 오버레이해서 보여준다.
- UITK(not IMGUI)
- 카메라 뷰 위에 평가가 뷰를 오버레이
- 평가 뷰는 과노출 영역을 zebra 패턴으로, 저조도 영역을 magenta tint 로 출력한다. 다른 영역은 transparent.
- 판정 기준은 아래와 같다
```
float luma = c.r * 0.2126 + c.g * 0.7152 + c.b * 0.0722;
if(luma > upper_limit){
	// draw zebra
}
else if(luma < lower_limit){
	// draw magenta tint
}
```
- 카메라 뷰, 평가 뷰와 함께 upper_limit, lower_limit을 편집할 수 있는 UI 출력
- SpinnakerPreviewController 와 다른 별개의 스크립트와 싼으로 작성할 것. evaluation 디렉토리에 애셑 배치.