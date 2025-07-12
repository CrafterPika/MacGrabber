# MacGrabber

Splatoon v288 Cemu PID Grabber for MacOS 

![image](demo.png)

# Compile

You'll need [brew.sh](https://brew.sh) to compile this project


Install mono using homebrew:
```
brew install mono
```

To compile the code:
```bash
mcs -out:MacGrabber.exe MacGrabber.cs -r:System.Net.Http -r:System.Xml.Linq
```

Run it while Splatoon is open in Cemu:
```
sudo mono ./MacGrabber.exe
```