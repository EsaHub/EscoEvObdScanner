# Esco EV OBD Scanner – käyttöohje

EscoEvObdScanner lukee sähköauton ohjainlaitteilta (ECU) tietoja OBD-liitännän
kautta. Se kysyy tunnisteita (DID) yksi kerrallaan, tallentaa vastaukset
tiedostoon ja auttaa selvittämään, mitä tavut tarkoittavat: esimerkiksi akun
jännitteen, varaustilan tai lämpötilan.

Ohjelma **vain lukee**. Se ei kirjoita autoon mitään, ei tyhjennä vikakoodeja
eikä avaa ohjainlaitteiden suojauksia.

Ohjelmasta on kaksi versiota:

- **Windows-sovellus** (`EscoEvObdScanner.App`): mittaristo, akkukuva, käyrät ja
  kaikki toiminnot napeilla. Suositeltu tapa. Ks. [luku 0](#0-windows-sovellus).
- **Komentoriviohjelma** (`EscoEvObdScanner`): lukeminen, skannaus, snapit,
  vikakoodit ja ohjainlaitehaku komentoina, pitkiä skannauksia ja
  jatkokäsittelyä varten; ei mittaristoa, käyriä eikä raporttia. Käännetään
  lähdekoodista, ei tule asennuspaketin mukana. Luvut 3–7.

> Kehitetty ja testattu OBDLink EX -USB-adapterilla (STN-piiri; vastaa `ATI`-kyselyyn
> "ELM327 v1.4b") vuoden 2023 Opel Mokka-e:llä ja vuoden 2024 Kia Niro EV:llä. Ainakin tämä adapteri
> toimii varmasti. Mokka-e:n tunnisteskannaukset on ajettu myös Bluetoothilla OBDLink LX
> -adapterilla (perinteinen Bluetooth, sama piiriperhe) yhtä nopeasti kuin USB:llä.

---

## Sisällys

0. [Windows-sovellus](#0-windows-sovellus)
1. [Mitä tarvitaan](#1-mitä-tarvitaan)
2. [Riskit – onko pelkkä lukeminen turvallista?](#2-riskit--onko-pelkkä-lukeminen-turvallista)
3. [Ohjelman ajaminen](#3-ohjelman-ajaminen)
4. [Ensimmäinen testi autossa, askel askeleelta](#4-ensimmäinen-testi-autossa-askel-askeleelta)
5. [Kaikki komennot](#5-kaikki-komennot)
6. [Valitsimet](#6-valitsimet)
7. [Tulostiedostot](#7-tulostiedostot)
8. [Vianetsintä](#8-vianetsintä)
9. [Auton tiedosto](#9-auton-tiedosto)
10. [Sanasto](#10-sanasto)

---

## 0. Windows-sovellus

### Asentaminen

Aja `EscoEvObdScanner-0.1.0.msi` ja hyväksy asennus. Sovellus tulee
Käynnistä-valikkoon ja työpöydälle nimellä **Esco EV OBD Scanner**. *Custom Setup* -sivulla
voi jättää työpöydän pikakuvakkeen pois ja vaihtaa asennuskansion (*Browse*). Asennus ei
tarvitse erillistä .NET-asennusta.

Ilman asennusta: `publish\app`-kansion `EscoEvObdScanner.App.exe` toimii
sellaisenaan, kun sen vieressä ovat `cars`- ja `help`-kansiot. Kansion voi kopioida minne tahansa.

Sovelluksen oikean yläkulman **?**-painike avaa lyhyen käyttöohjeen (myös Käynnistä-valikossa): sovelluksen käyttö, raportti ja sen rajat lähteineen sekä vianetsintä. Tämä OHJE kattaa lisäksi komentorivityökalun ja auton tiedostot.

### Ensimmäinen käynnistys

1. **Yhteys**-välilehti: lisää ajoneuvo (*Lisää ajoneuvo…*): anna autolle nimi, esim.
   rekisterinumero tai "Työauto", ja valitse sen alusta (auton tiedosto). Jokaisella
   ajoneuvolla on oma tuloskansio ja oma ajohistoria range-arviota varten. Saman alustan autot
   käyttävät samaa auton tiedostoa. Ensimmäisellä yhteydellä sovellus tallentaa ohjainlaitteen
   sarjanumerosta tai VIN-numerosta tunnisteeksi tiivisteen (tunnisteeseen ei tallennu itse numero; akunhallinta ensin) ja varoittaa
   myöhemmin, jos adapteri on eri autossa kuin valittu ajoneuvo. Jos tunniste ei täsmää mutta auto on
   varmasti sama (esim. akunhallinta on vaihdettu), varoituksen *Kyllä* päivittää tunnisteen. Jos auto ei kerro kumpaakaan,
   tunnistus jää tekemättä ja loki kertoo sen. Jos mikään auton tiedoston ohjainlaite ei vastaa, sovellus varoittaa:
   ajoneuvolla on luultavasti väärä auton tiedosto, tai auto ei ole päällä. *Poista ajoneuvo* poistaa ajoneuvon listasta; sen tulokset jäävät kansioonsa.
2. Valitse portti. Sovellus näyttää laitteen nimen, joten
   OBD-adapteri erottuu muista porteista (esim. *COM4 – USB Serial Port*).
3. Paina **Yhdistä**. Yläpalkin pallo muuttuu vihreäksi ja näyttää adapterin
   version; kun adapteri on käytössä (esim. seuranta), pallo on keltainen.
4. **Mittaristo** → **Aloita seuranta**.

**Ei adapteria tai autoa käsillä?** Valitse autolistasta **Stellantis e-CMP –
demoauto (simuloitu)** ja paina **Yhdistä**. Se on simuloitu Mokka-e oikeista
mittauksista ja vikakoodeista, ja kaikki välilehdet toimivat sillä ilman adapteria. Demoauton
tulokset tallentuvat omaan kansioonsa `stellantis-ecmp-demo`, erilleen oikean
auton tuloksista, eikä sen skannauksia voi lisätä auton tiedostoon.

### Välilehdet

Kun seuranta on käynnissä, se varaa adapterin: välilehdillä, jotka lukevat autoa (Vikakoodit,
Ohjainlaitteet, Etsintä, Vertailu), näkyy silloin ikkunan alareunassa keltainen ilmoitus ja *Lopeta seuranta* -painike.

| Välilehti | Mitä näet |
|---|---|
| **Yhteys** | Ajoneuvon valinta ja lisäys (myös demoauto), portti, nopeus, yhdistäminen, auton tiedoston sijainti ja tuonti, loki. *Näytä adapterin liikenne* näyttää jokaisen komennon ja vastauksen. |
| **Mittaristo** | *Aloita seuranta automaattisesti* käynnistää seurannan itse heti, kun yhteys adapteriin on muodostettu (läppäri voi jäädä autoon koskematta); ei kuitenkaan, jos mikään auton tiedoston ohjainlaitteista ei vastaa. Teho, nopeus (ja moottorin kierrosluku), varaus (ja range-arvio, kun auto kertoo akun jäljellä olevan energian; e-CMP) ja virta mittareina. Mittarin otsikko kertoo suunnan: *Latausteho*, *Ajoteho*, *Talteenotto* tai *Kulutus, seis*. Muut luvut ovat ryhmissä: *Ajo* (vaihde, ajotila, kulutus, ulkolämpö, mittarilukema), *Range* (range-arvio nykyisellä varauksella aiempien ajojen ja tämän ajon kulutuksella), *Lataus* (latausjohdon tila, verkkojännite, arvioitu aika täyteen, *Ladattu (tämä seuranta)* eli latauksessa akkuun ladattu kWh, sekä latausraja, kun auton tiedosto kertoo sen), *Akku* (jännite, jäljellä oleva energia, akun kunto, lämpötilat, kennoero, alin kenno, purkuraja kun auton tiedosto kertoo sen, eristysvastus, 12 V:n akku) ja *Lämpötilat*, kun auton tiedosto kertoo voimalinjan lämpötiloja. *Matka (tämä seuranta)* näyttää matkan, käytetyn ja talteen otetun energian, nettokulutuksen, keskikulutuksen ja sen, paljonko varausta kului; *Huiput (tämä seuranta)* huipputehon ja talteenoton huipun. Kun auton tiedosto kertoo auton omat laskurit, näkyvät lisäksi *Koko ikä (auton omat laskurit)*: akusta otettu ja akkuun mennyt energia sekä keskikulutus, ja *Sähkö (arvio)*: verkosta otettu energia ja sähkö yhteensä sekä verkosta otettu energia ja sähkö vuodessa. Arvio käyttää annettua sähkön hintaa (snt/kWh) ja latauksen häviötä (%); vuotta kohden se lasketaan käyttöönottopäivästä ja silloisesta mittarilukemasta (käytettynä ostetussa autossa oma osuus lasketaan sen jälkeen ajetuista kilometreistä). Akkuun mennyt sisältää jarrutuksen talteenoton, joten arvio lasketaan akusta otetusta energiasta. |
| **Akku** | Kaikki kennot ruudukkona värikoodattuna: pienin ja suurin kehystettynä, arvo näkyy hiiren alla. Kennojen ja lämpötila-anturien määrä on se, montako arvoa auton vastauksessa on; auton tiedosto kertoo, mistä ne luetaan (e-CMP: 108 kennoa ja 54 anturia). *Kennojen kunto* vaihtaa näkymän kennojen kuntoon, jos auton tiedosto kertoo sen paikan. *Kennot kuormassa* näyttää, kuinka paljon kukin kenno notkahtaa kuormassa muita enemmän (mV / 100 A, levossa oleva ero vähennettynä): vihreä hyvä, keltainen seurattava, punainen huono (kun akun kylmin anturi on kuormassa alle 10 °C, ei punaista, koska kylmässä kennojen ero kuormassa kasvaa). Se kertyy seurannan aikana; *Avaa tallenne…* näyttää saman aiemmasta seurannasta ja *Näytä seuranta* palaa käynnissä olevaan. Alhaalla lämpötila-anturit. *Luo raportti (PDF)* tekee akkuraportin näkyvistä arvoista (ks. alla). |
| **Käyrät** | Teho, nopeus, virta, varaus, jännite, kennoero ja lämpötilat ajan funktiona. Aikaikkuna 1–30 min. |
| **Vikakoodit** | Auton tiedoston ohjainlaitteiden vikakoodit värein: punainen = vika nyt, keltainen = vika ollut. Vian tyyppi ja tilabitit näkyvät standardien nimillä kääntämättä (SAE J2012-DA, ISO 14229-1), jotta ne löytyvät hakemalla; *Tarkoittaa*-sarake selittää tilabitit suomeksi. Laitteet, jotka eivät ole koskaan vastanneet tähän autoon, ohitetaan, ellei *Kysy myös laitteilta, jotka eivät ole vastanneet* ole valittuna. *Syvä haku* lukee myös tilannekuvat; e-CMP-autoissa *Syntyi*-sarake näyttää niistä vian syntyhetken kilometrit ja päivämäärän. Testit, joita laite ei ole vielä ajanut, ovat oletuksena piilossa. *Aiemmat luennat* -listan *Näytä* avaa tallennetun luennan uudelleen. |
| **Ohjainlaitteet** | Auton tiedoston ohjainlaitteet ja niiden tunnisteet: tunnetut ja skannauksissa löydetyt. *Testaa valittujen laitteiden yhteys* kysyy jokaiselta valitulta laitteelta, vastaako se, ja merkitsee nimen eteen ✓ tai ✗ (hyvä ensimmäinen yhteystesti). Vasemmalla valitaan ohjainlaitteet (*Kaikki ohjainlaitteet* valitsee tai poistaa kaikki), ylhäällä näytetäänkö *Tunnetut*, *Kaikki* vai *Tuntemattomat*, ja hakukenttä rajaa lisää. Harvoin muuttuvat arvot (esim. versiot ja asetukset) näkyvät vain, kun *Myös harvoin muuttuvat* on valittuna, ja tunnisteet, joiden arvon ohjainlaite kielsi skannauksessa, vain valinnalla *Myös kielletyt*. Laitteet, jotka eivät ole koskaan vastanneet tähän autoon, ovat listan lopussa omana ryhmänään (*Eivät ole vastanneet*) eivätkä ole valittuina. **Lue** hakee autosta näkyvien rivien arvot kerran, laite kerrallaan; ohjeteksti kertoo määrän ja keston. Valitun rivin kaava, muistiinpanot ja aiemmat näytteet näkyvät alhaalla. *Varmuus*-sarake: *varmistettu* (tarkistettu oikeasta autosta mittaristoa tai muuta tunnettua arvoa vasten: testiauto tai muut lähteet), *tarkistamaton* (nimi tai kaava yhdestä lähteestä), *ehdokas* (kaava on valistunut arvaus) tai *tuntematon* (merkitystä ei tiedetä). *Lukuina*-sarake näyttää raakatavut tavallisina lukuina tai tekstinä, mikä helpottaa tuntemattomien arvaamista. |
| **Etsintä** | Tutkimustyökalu uusille autoille ja tunnisteille. **1. Etsi ohjainlaitteita** käy läpi osoitealueen (oletuksena auton tiedoston alue, *Laaja alue* täyttää koko sallitun alueen) ja listaa vastanneet, uudet merkittynä; se kertoo myös, mitkä alueen osoitteet auton tiedoston mukaan on jo etsitty (`ecu_search`). Haku lähettää jokaiseen osoitteeseen "oletko siellä?" -kyselyn ja hiljaiselle osoitteelle vielä kysymyksen laitteen tilasta; kumpikaan ei muuta autossa mitään (komennot: kohta *Mitä ohjelma lähettää autolle*). **2. Etsi tunnisteita ohjainlaitteesta** käy läpi yhden laitteen tunnistealueen. Jokainen skannaus tallentuu jo sen aikana omaan tiedostoonsa tietokansioon, auton `scans`-alikansioon; auton tiedostoon löydöt lisätään vain *Lisää auton tiedostoon* -painikkeella. Ohjainlaitehaku ei itse tallenna mitään: löydöt näkyvät listassa ja lokissa, ja *Lisää uudet auton tiedostoon* lisää UUSI-merkityt laitteet auton tiedostoon nimellä "Tuntematon laite", jolloin niiden tunnisteita voi etsiä kohdassa 2. |
| **Vertailu** | *Ota snap* lukee kerran auton tiedoston tunnisteet (tunnetut ja löydetyt) ja tallentaa ne. Harvoin muuttuvat ja kielletyt tunnisteet jäävät pois, ellei *Lue myös harvoin muuttuvat* tai *Lue myös kielletyt* ole valittuna; ohjeteksti kertoo määrän ja keston. Ota snap ennen ja jälkeen jonkin muutoksen (lataus, lämmitys, valot) ja vertaa niitä: lista näyttää, mitkä arvot muuttuivat ja kuinka paljon. Tunniste, joka muuttuu vain muutoksen mukana, liittyy todennäköisesti siihen. |

Kieli vaihtuu yläkulman **FI / EN** -napeista.

### Akkuraportti (PDF)

Akku-välilehden *Luo raportti (PDF)* tekee raportin siitä, mitä sovellus näyttää
juuri nyt: akun arvot, jokainen kenno ja lämpötila-anturi sekä Vikakoodit-välilehdellä
luetut vikakoodit. Raportti toimii jokaiselle autolle, jolle on auton tiedosto;
kohdat, joita auton tiedosto ei kerro, jäävät pois. Kieli on käyttöliittymän kieli.

1. Käynnistä seuranta ja anna kennojen päivittyä.
2. Lue vikakoodit Vikakoodit-välilehdellä, niin ne tulevat mukaan.
3. Kirjoita halutessasi kenttään *Teksti raporttiin* esim. kenen auto (tulostuu auton nimen alle) ja paina *Luo raportti (PDF)*.

Raportti avautuu ja tallentuu `reports`-kansioon. Seurannan voi pysäyttää ennen
painamista; raportti käyttää viimeisintä lukemaa.

Jos seuranta on ollut päällä ajon aikana, raportissa on myös *Ajon aikana* -osio:
kesto ja aikajakso, matka, energia ja kulutus, suurin kuorma, akun lämpeneminen sekä kennoero
levossa ja kuormassa. *Kennot kuormassa* listaa kennot, jotka jäävät suurella virralla
eniten keskiarvon alle sen lisäksi, mitä ne ovat sen alla levossa. Taulukon sarakkeet
laskevat sen vasemmalta oikealle: *Kuormassa alle keskiarvon* − *Levossa alle keskiarvon* =
*Kuormassa lisää*. Vain tämä lisä kertoo sisäisestä vastuksesta; levossa matala kenno on vain
vajaammin varattu, ja se näytetään erikseen. Alle 5 mV:n erot ovat mittaustarkkuuden rajoissa.
Auto, joka ilmoittaa kennojännitteet karkeammin (Kia 20 mV:n portain), nostaa rajan puoleen
portaasta, ja värirajat nousevat sen mukaan; kennoero levossa näkyy silloin esim. *alle 20 mV*.
Kunto kertoo kapasiteetista, joten kenno voi olla täysikuntoinen ja silti notkahtaa
muita enemmän. Kennojen järjestämiseen tarvitaan vähintään kolme kennoluentaa sekä
kuormassa että levossa. Jos viimeisin lukema on kuormassa, kennoero
arvioidaan istunnon viimeisimmästä lepohetkestä.

*Raportti tallenteesta…* tekee saman raportin jälkikäteen tallennetusta seurannasta. Lista
näyttää jokaisesta seurannasta ajan, keston, kilometrit ja kennolukemat, joten ajon tunnistaa
kilometreistä ja kuormalukemista.

Yhteenveto arvioi akun kunnon, kennojen jännite-eron, kennot kuormassa (kun seuranta oli päällä ajon aikana),
lämpötilaeron, eristysvastuksen ja akun ohjainlaitteiden vikakoodit (ohjainlaitteet, joilta auton tiedosto lukee akun
arvot). Muiden ohjainlaitteiden koodit näkyvät listassa, mutta eivät vaikuta arvioon.
Jos akunhallinta ei vastannut tai luenta jäi kesken, akun vikakoodeja ei arvioida;
löytyneet viat lasketaan silti, ja raportti kertoo, että niitä voi olla enemmän.
Tallennetusta luennasta (*Aiemmat luennat*) ei näe, vastasiko akunhallinta, joten
ilman akun vikoja se näytetään vain tietona; raportti kertoo luennan ajankohdan.
Kennojen jännite-ero arvioidaan vain levossa (virta enintään 5 A); kuormassa se näytetään
tietona. Raportti kertoo jokaisen rajan perusteen (ks. alla).

Jos auto kertoo elinaikaiset laskurinsa, raportissa on osio *Koko ikä (auton omat laskurit)*.
Kia Niro EV ja Stellantis e-CMP: akkuun mennyt energia (sisältää jarrutusten talteenoton) ja
akusta otettu energia. Kia Niro EV lisäksi: AC- ja DC-latauskerrat ja niillä ladattu
energia sekä niistä laskettuna pikalatauksen osuus ja ladattu energia ajettua matkaa kohti.
Stellantis lisäksi: käynnistyskerrat (HV-kontaktorien sulkemiset) ja aika yli 55 °C (0 = ei kertaakaan).

Ryhmässä *Akku ja akunhallinta* on akunhallinnan valmistuspäivä, siitä laskettu ikä ja
kulumisvauhti ((100 % − SOH) vuotta ja 10 000 km kohti; olettaa uuden akun kunnoksi 100 % ja akun
yhtä vanhaksi kuin akunhallinnan) sekä
akunhallinnan ohjelmisto- ja laitteistoversio (Stellantis). Ne luetaan yhteyden alussa ja
tallennetaan ajoneuvon tietoihin kuten VIN, joten myös tallenteesta tehty raportti saa ne; silloin niiden
perässä on lukupäivä. 12 V:n akun jännitteen perässä lukee *lepojännite*, kun auto seisoo, akusta ei
kulje virtaa (enintään 5 A) ja jännite on alle 13 V, ja *DC/DC lataa*, kun jännite on 13 V tai yli;
lepojännitteen alla on vertailuarvot (täysi noin
12,65 V, puolillaan noin 12,24 V, Battery University BU-903). Ne ovat suuntaa antavia: luotettava
lukema vaatii tuntien levon, ja auton omat kuormat laskevat jännitettä.

Raportin alussa on auton VIN-numero, jonka sovellus lukee yhteyden alussa. Sen voi
verrata tuulilasin alakulmaan ja rekisteriotteeseen, joten raportti on sidottu mitattuun
autoon. VIN tallentuu ajoneuvon tietoihin, ellei tunnistus totea autoa toiseksi, joten myös
jälkikäteen tehty raportti saa sen (*VIN … (luettu 4.10.2026)*). Jos tunnistus ei voi varmistaa
autoa samaksi, eri VIN ei korvaa tallennettua. Jos auto ei kerro VIN-numeroa, raportti sanoo
sen, ja numeron voi kirjoittaa kenttään *Teksti raporttiin*. Suomenkielisessä raportissa
päivämäärät ja luvut ovat suomalaisessa muodossa (4.10.2026 17.58), englanninkielisessä
Windowsin aluemuodossa.

Taulukoiden *Lähde*-sarake kertoo jokaisesta rivistä, onko luku auton ilmoittama (*auto*)
vai ohjelman laskema (*laskettu*); kennoruudukoiden, kennot kuormassa -taulukon ja vikakoodien
alla sama kerrotaan lauseella. Raportin lopussa *Näin luvut on laskettu* antaa jokaisen lasketun luvun kaavan, ja *Arvioiden
rajat* listaa jokaisen arvion rajat ja niiden perusteen: lähde, jos sellainen on (esim. eristysvastuksen
raja UNECE R100 -säädöksestä, kennoeron ja lämpötilaeron suuruusluokat alan lähteistä), ja muuten
maininta, että raja on ohjelman oma. Valmistajan raja-arvoja ne eivät ole. Lähteet ovat README:ssä. *Mistä luvut tulevat* erottelee:

- **Auton ilmoittamat:** varaus, SOH, akun energia, jännite, virta, lataus- ja
  purkuraja, kennojännitteet ja kennojen kunto, lämpötilat, eristysvastus, 12 V:n akun jännite,
  latausjohdon tila, matkamittari, vikakoodit, akunhallinnan valmistuspäivä ja versiot sekä
  auton laskurit koko käyttöajalta. Ne näytetään sellaisinaan, auton tiedoston kaavoilla
  yksiköiksi muunnettuina.
- **Ohjelman laskemat:** teho, kennoero, lämpötilaero, akun keskilämpötila anturien
  keskiarvona (kun auto ei kerro sitä), *Ajon aikana* -osion luvut, kennot kuormassa,
  laskureista pikalatauksen osuus ja ladattu energia ajettua matkaa kohti sekä ikä
  akunhallinnan valmistuspäivästä. Suoraa laskentaa auton arvoista ilman oletuksia.
- **Ohjelman arviot:** arvosanat, värit ja kokonaisarvio *Arvioiden rajat* -kohdan rajoilla,
  12 V:n akun jännitteestä, nopeudesta ja akun virrasta päätelty tila (lepojännite tai DC/DC lataa) sekä kulumisvauhti, joka
  olettaa uuden akun kunnoksi 100 % ja akun yhtä vanhaksi kuin akunhallinnan.

### Vastuunrajaus

Sovellus lukee ja näyttää, mitä auto ilmoittaa, eikä voi taata sen oikeellisuutta.
Laskelmat ja arviot ovat suuntaa antavia. Raportti ei ole valmistajan tai korjaamon
akkutesti eikä takuu akun kunnosta, ja PDF-tiedostoa voi muokata. Auton kaupassa
luotettavin tulos saadaan, kun ostaja on itse mukana mittauksessa. Sovellus on
ilmainen ja toimitetaan sellaisenaan, ilman takuuta (MIT-lisenssi).

### Mihin tiedostot tallentuvat

| Mitä | Missä |
|---|---|
| Tulokset, ajoneuvon ja tyypin mukaan alikansioihin | `Dokumentit\EscoEvObdScanner\<ajoneuvo>` |
| Autojen tiedostot (omat työkopiot, alikansio `cars`), ajoneuvot (`vehicles.json`), asetukset ja ajohistoria | `%LOCALAPPDATA%\EscoEvObdScanner` |

Tuloskansion alikansiot:

| Kansio | Sisältö |
|---|---|
| `scans` | skannaukset |
| `snaps` | snapit |
| `dtc` | vikakoodiluennat |
| `live` | seurannan CSV-tallenteet (arvot, kennot ja kennojen kunto) |
| `watch`, `scaling` | komentorivin `watch`- ja `scaling`-tulokset |
| `logs` | lokit |
| `reports` | akkuraportit (PDF) |

Sovellus tallentaa tulokset ajoneuvon kansioon (`<ajoneuvo>`), komentorivi auton
tiedoston nimiseen kansioon (`<auto>`). Komentoriviltä otettu snap näkyy siis
sovelluksen Vertailu-välilehdellä vain, kun sovelluksessa ei ole valittu ajoneuvoa.
Autotiedostoja ne eivät jaa: sovellus käyttää omia työkopioitaan
(`%LOCALAPPDATA%\EscoEvObdScanner\cars`), komentorivi nykyisen kansion tai exen
vieressä olevan `cars`-kansion tiedostoja. Komentorivin `merge` ei siksi näy
sovelluksessa, eikä sovelluksen *Lisää auton tiedostoon* komentorivillä.

Seuranta tallentaa jokaisen lukukierroksen CSV-tiedostoon, joka aukeaa
Excelissä. Sarakkeet on eroteltu puolipisteellä, ja luvuissa on desimaalipiste.

---

## 1. Mitä tarvitaan

| Mitä | Huomioita |
|---|---|
| Windows-kone | Sovellus (MSI tai exe) ei tarvitse muuta. Komentoriviversio tarvitsee .NET 10 SDK:n, joka tulee Visual Studion mukana. |
| ELM327- tai STN-adapteri | USB tai klassinen Bluetooth. Adapterin pitää näkyä Windowsissa COM-porttina. Wi-Fi-adapterit ja Bluetooth LE (4.0) -adapterit eivät toimi, koska Windows ei tee niistä COM-porttia. Ohjelma on kehitetty ja testattu OBDLink EX -USB-adapterilla, joten se toimii varmasti. **Tärkein ero adapterien välillä:** adapterin pitää tukea pitkiä, monesta kehyksestä koostuvia vastauksia ja vuonohjauksen komentoja `ATFCSH`, `ATFCSD` ja `ATFCSM`, sillä kennojännitteet tulevat niin (e-CMP:ssä 108 kennoa yhdessä 243 tavun vastauksessa). Ilman niitä varaus ja jännitteet näkyvät, mutta kennot eivät. Testaa uusi adapteri Akku-välilehdeltä. |
| Auto | Auto, jolle on auton tiedosto (ks. [Auton tiedosto](#9-auton-tiedosto)). Mukana on Stellantis e-CMP (Peugeot e-208/e-2008, Opel Corsa-e/Mokka-e, Citroën e-C4, DS 3 E-Tense). Testattu vuoden 2023 Mokka-e:llä ja vuoden 2023 Citroën e-C4:llä; muissa e-CMP-autoissa osoitteet ja kaavat ovat todennäköisesti samat, mutta sitä ei ole varmistettu. Lisäksi Kia e-Niro / Hyundai Kona Electric 2018–2022 (`kia-eniro`), evDashin julkaisemien paikkojen pohjalta, ja Kia Niro EV 2022– (`kia-niro-ev-2022`), muiden yhdysvaltalaisista autoista tallentamien vastausten pohjalta, testattu vuoden 2024 eurooppalaisella Niro EV:llä. e-Niroa ei ole testattu autossa. |

### COM-portin selvittäminen

1. Kytke adapteri koneeseen. Bluetooth-adapteri pitää ensin pariuttaa;
   PIN-koodi on yleensä `1234` tai `0000` (OBDLink LX: paina adapterin pair-nappia).
   - Windows 11 ei näytä OBD-adapteria *Lisää laite* -haussa, ennen kuin kohdassa
     *Asetukset → Bluetooth ja laitteet → Laitteet → Bluetooth-laitteiden haku*
     on valittu **Lisäasetukset** (englanninkielisessä Windowsissa
     *Bluetooth devices discovery* → **Advanced**).
   - Bluetooth-adapteri saa virran autosta: sen pitää olla OBD-liittimessä ja auton päällä.
2. Avaa **Laitehallinta** (Device Manager) → **Portit (COM ja LPT)**.
3. Etsi adapteri, esimerkiksi *USB-SERIAL CH340 (COM3)* tai *Standard Serial
   over Bluetooth link (COM5)*. Sulkeissa oleva nimi on portti.
   - Bluetooth luo yleensä kaksi porttia. Käytä **lähtevää** (Outgoing)
     porttia. Näet sen Bluetooth-asetuksista kohdasta *Lisää Bluetooth-asetuksia
     → COM-portit*.

### Nopeus (baud)

Ohjelma käyttää oletuksena nopeutta 115200. Monet halvat ELM327-kopiot
toimivat nopeudella 38400. Sovelluksessa nopeus valitaan Yhteys-välilehden
kentästä *Nopeus (baud)*; komentorivillä lisää komentoon `--baud 38400`.

### OBD-liitännän nastat

Jotkin vanhemmat e-CMP-autot tuovat diagnostiikkaväylän OBD-liittimen
nastoihin 3 ja 8 tavallisten nastojen 6 ja 14 sijaan. Silloin tarvitaan
erillinen adapterikaapeli. Uudemmilla ohjelmistoversioilla tavallinen liitäntä
toimii.

---

## 2. Riskit – onko pelkkä lukeminen turvallista?

**Lyhyesti:** lukeminen on pienen riskin toimintaa. Se on samaa, mitä
OBD-sovellukset (Car Scanner, Torque ym.) tekevät. Riski ei silti ole nolla,
ja suurin käytännön riski on **12 V:n akun tyhjeneminen**, ei ohjainlaitteen
rikkoutuminen.

### Mitä ohjelma lähettää autolle

| Palvelu | Mitä tekee | Muuttaako mitään autossa? |
|---|---|---|
| `0x22` ReadDataByIdentifier | Lukee yhden arvon | Ei |
| `0x24` ReadScalingDataByIdentifier | Kysyy, miten arvo koodataan | Ei |
| `0x19` ReadDTCInformation | Lukee tallennetut vikakoodit | Ei |
| `0x3E` TesterPresent | ”Oletko siellä?” – vain ohjainlaitehaku (`ecus`; Ohjainlaitteet-välilehden *Testaa valittujen laitteiden yhteys* ja Etsintä-välilehti) | Ei |

### Mitä ohjelma ei lähetä koskaan

| Palvelu | Miksi vaarallinen |
|---|---|
| `0x14` ClearDiagnosticInformation | Tyhjentää vikakoodit ja niiden historian |
| `0x27` SecurityAccess | Väärät avaimet voivat lukita ohjainlaitteen pitkäksi aikaa |
| `0x2E` WriteDataByIdentifier | Kirjoittaa arvoja ohjainlaitteelle |
| `0x31` RoutineControl | Käynnistää rutiineja (testejä, kalibrointeja) |
| `0x10` DiagnosticSessionControl | Vaihtaa diagnostiikkaistuntoa – ohjelma pysyy aina oletusistunnossa |
| `0x11` ECUReset | Käynnistää ohjainlaitteen uudelleen |
| `0x2F` InputOutputControlByIdentifier | Ohjaa toimilaitteita (esim. puhaltimia, releitä) |
| `0x34`–`0x37` RequestDownload … RequestTransferExit | Ohjelmiston lataus ohjainlaitteelle |
| `0x85` ControlDTCSetting | Kytkee vikakoodien tallennuksen pois |

Näitä ei ole koodissa ollenkaan, joten niitä ei voi lähettää vahingossakaan.

### Todelliset riskit ja miten ne vältetään

| Riski | Mitä voi tapahtua | Miten vältät |
|---|---|---|
| **12 V:n akku tyhjenee** | Kyselyt pitävät auton hereillä. Sähköautossa ajoakku ei lataa 12 V:n akkua, jos auto ei ole READY-tilassa. Tyhjä 12 V:n akku = auto ei käynnisty ollenkaan. | Pidä auto **READY-tilassa** tai latauksessa koko ajon ajan. Irrota adapteri, kun lopetat. |
| **Näytöt sekoavat ajon aikana** | Jotkin e-208:n ja Corsa-e:n omistajat kertovat, että ajon aikana käytetty OBD-adapteri on sekoittanut infotainmentin tai näytöt. Omalla Mokka-e:llä näin ei ole käynyt. | Jos näytöt käyttäytyvät oudosti seurannan aikana, lopeta seuranta ja irrota adapteri. |
| **Hälytin** | Joissakin autoissa kyselyt sytytysvirta pois päältä ovat laukaisseet hälyttimen (raportoitu muilla automerkeillä). | Tee kaikki ajot auto READY-tilassa ja ovet lukitsematta. |
| **`ecus`-komento lähettää viestejä vieraille tunnuksille** | Haku lähettää TesterPresent-viestin jokaisella tunnuksella auton tiedoston alarajasta (`probe_from`, e-CMP 0x600, Kia 0x700) 0x7EF:ään. Alempia tunnuksia ei hyväksytä, koska osa niistä on auton tavallisia viestejä: haku niillä voi saada ohjainlaitteen tallentamaan vikakoodin. Sovelluksen Ohjainlaitteet-välilehden *Testaa valittujen laitteiden yhteys* kysyy alarajan alla olevat tunnetut osoitteet (e-CMP:ssä esim. laturi 0x590) yksitellen. | Aloita kapealla alueella: `ecus 6A0 6BF`. |
| **Tietoliikennevikakoodi** | Ohjainlaite voi tallentaa tietoliikennevian (U-koodin), jos väylälle tulee sille outo viesti. Tavalliset lukukyselyt eivät sitä aiheuta; siksi ohjainlaitehaku on rajattu diagnostiikkaosoitteisiin (ks. edellinen rivi). | Lue vikakoodit ennen ja jälkeen. Syvän haun *Syntyi*-sarakkeesta näet, milloin vika syntyi. |
| **Huono adapteri** | Halvat kopiot voivat jumittua tai antaa roskaa. Auto ei kärsi, mutta tulokset voivat olla vääriä. | Käytä `--trace`-valitsinta ja katso, mitä adapteri oikeasti vastaa. |
| **Adapterin virta tai yhteys katkeaa kesken** | Jos adapterin virta notkahtaa (auto sammuu tai käynnistyy, 12 V:n akku on heikko) tai Bluetooth-yhteys katkeaa juuri lähetyksen aikana, komennosta voi pudota merkki. Vajaa komento hylätään yleensä virheenä, mutta pieni jäännösriski on, että autolle menee jokin muu viesti kuin ohjelman lukukysely. | Pidä auto READY-tilassa tai latauksessa koko ajon ajan, älä sammuta tai käynnistä autoa kesken skannauksen ja pidä kannettava lähellä adapteria. |
| **Ajaminen** | Kannettavan käyttö ajon aikana vie huomion. | `watch`-komentoa ajon aikana käyttää **matkustaja**, ei kuljettaja. |

---

## 3. Ohjelman ajaminen

### Tapa A: Visual Studiosta (helpoin)

Projektissa on valmiit ajoprofiilit.

1. Avaa `EscoEvObdScanner.slnx` Visual Studiossa.
2. Valitse yläpalkin vihreän ▶-napin vierestä avautuvasta valikosta profiili,
   esimerkiksi **2 known COM4**.
3. Paina ▶ tai F5.

Profiileissa on portti `COM4`. Jos adapterisi on eri portissa, vaihda se:
**Debug → EscoEvObdScanner Debug Properties** tai suoraan tiedostoon
`EscoEvObdScanner\Properties\launchSettings.json`.

Profiilit ajetaan projektikansiossa, jotta `merge` päivittää projektin
auton tiedostoa (`cars`-kansio). Tulokset tallentuvat samaan `Dokumentit\EscoEvObdScanner`-kansioon
kuin sovelluksessa.

### Tapa B: komentoriviltä

Avaa PowerShell tai komentokehote **projektikansiossa** (siinä, jossa
`cars`-kansio on):

```powershell
cd <repon kansio>\EscoEvObdScanner
dotnet run -- COM3 known
```

Kaikki `--`-merkin jälkeen menee ohjelmalle. Muut esimerkit:

```powershell
dotnet run -- COM3 ecus 6A0 6BF
dotnet run -- COM3 scan d8xx bms
dotnet run -- - blocks
```

Voit myös ajaa valmiiksi käännetyn ohjelman suoraan:

```powershell
.\bin\Debug\net10.0\EscoEvObdScanner.exe COM3 known
```

### Komennon rakenne

```
EscoEvObdScanner <portti> <komento> [lisätiedot] [valitsimet]
```

- **Auto:** jos `cars`-kansiossa on useampi auto, lisää jokaiseen komentoon (myös `ecus`) `--car <nimi>`, esimerkiksi `--car stellantis-ecmp`, `--car kia-eniro` tai `--car kia-niro-ev-2022`. Tämän ohjeen esimerkeistä se on jätetty pois.
- `<portti>` on esimerkiksi `COM3`. Komennot, jotka eivät tarvitse adapteria
  (`blocks`, `merge`, `diff`, `cars`), saavat portiksi viivan `-`.
- Portti `demo` ajaa komennon simuloitua autoa vastaan, joten kaikkia komentoja
  voi kokeilla ilman adapteria: `dotnet run -- demo known`. Tulokset menevät
  demoauton omaan kansioon (`stellantis-ecmp-demo`).
- Tunnisteet (DID) ja CAN-tunnukset annetaan heksana, `0x`-etuliitteen kanssa
  tai ilman: `D815` ja `0xD815` tarkoittavat samaa.
- Ohjelman voi keskeyttää milloin tahansa painamalla **Ctrl+C**. Skannauksen ja
  seurannan tiedostot pysyvät ehjinä, koska jokainen rivi tallennetaan heti;
  kesken jäänyt snap jätetään pois, koska se ei kuvaisi koko autoa.

---

## 4. Ensimmäinen testi autossa, askel askeleelta

Tee nämä järjestyksessä. Jokainen askel todistaa, että edellinen toimii.

### Ennen autolle menoa

```powershell
dotnet run -- - blocks
```

Tämä ei tarvitse adapteria. Jos näet skannaussuunnitelman, ohjelma ja
auton tiedosto toimivat.

### Autossa

**0. Valmistelu**

- Auto **READY-tilaan**. Pelkkä sytytysvirta ei riitä.
- Kytke adapteri OBD-liittimeen. Se on e-CMP-autoissa yleensä kuljettajan
  jalkatilassa ohjauspyörän alla.
- Kytke adapteri koneeseen ja tarkista COM-portti.

**1. Toimiiko yhteys? – `known`**

```powershell
dotnet run -- COM3 known --trace
```

- Ensin näkyy adapterin versio, esimerkiksi `Adapter: ELM327 v1.5`. Jos
  tulee ilmoitus *The adapter does not answer*, kokeile `--baud 38400`.
- Sen jälkeen jokainen auton tiedoston tunniste luetaan kerran.
- **Onnistuminen:** ohjelmistoversio ja sarjanumero näkyvät luettavana tekstinä ja
  varaustila järkevänä prosenttina.
- Jos kaikki riveistä sanovat `no-data`, katso [Vianetsintä](#8-vianetsintä).

**2. Tallennetut vikakoodit – `dtc`**

```powershell
dotnet run -- COM3 dtc
```

Tästä saat vertailukohdan: aja sama komento lopuksi uudelleen ja katso,
ettei uusia vikakoodeja ole tullut.

**3. Ensimmäinen skannaus – `scan`**

```powershell
dotnet run -- COM3 scan d8xx bms
```

Käy läpi 256 tunnistetta akunhallinnasta. Kestää alle minuutin.

**4. Tulokset auton tiedostoon – `merge`**

```powershell
dotnet run -- - merge scan_bms_D800-D8FF_20260927_101500.jsonl
```

Vaihda tiedoston nimeksi se, jonka `scan` tulosti lopuksi. Pelkkä nimi riittää,
koska `merge` etsii sen `scans`-, `snaps`- ja `watch`-kansioista. Kaikki kerralla: `merge scan_*.jsonl`.

**5. Lopuksi**

```powershell
dotnet run -- COM3 dtc
```

Vertaa kohdan 2 tulokseen. Irrota adapteri.

---

## 5. Kaikki komennot

### `known` – lue tunnetut arvot

```
EscoEvObdScanner <portti> known
```

Lukee jokaisen auton tiedostossa (osio `dids`) olevan tunnisteen kerran ja
näyttää arvon tiedoston laskukaavalla muunnettuna.

**Aloita aina tästä.** Luettavat sarjanumero ja ohjelmistoversio sekä järkevä varaustila todistavat, että
adapteri, kaapeli, protokolla ja osoitteet ovat kunnossa.

Esimerkkituloste:

```
Battery management (BMS)  (0x6B4 -> 0x694)
  0xD410  State of charge, calibrated     78,13 %     raw 9C40
  0xD862  State of charge A (candidate)  72,40 %   ? raw 90CD
  0xF195  Software version              "A0BMUC059S74CV20"
```

Komentorivi kirjoittaa englanniksi; nimet tulevat auton tiedostosta, ja luvut
näytetään Windowsin numeromuodossa. Tiedostoon tai putkeen ohjattu tuloste on UTF-8:aa
(°, ä, Ω); Windows PowerShell 5.1:ssä aseta ensin `[Console]::OutputEncoding = [Text.Encoding]::UTF8`.

- `?` arvon perässä tarkoittaa, että laskukaava on arvaus tai vain yhden
  lähteen varassa (`confidence` = `unverified` tai `reported`).
- `raw` on ECU:n lähettämät tavut sellaisenaan.
- `rejected: ...` tarkoittaa, että ECU kieltäytyi (ks. [Sanasto](#10-sanasto)).

Tuloste tallentuu lokiin (`logs\known_<aika>.log`).

---

### `ecus` – etsi ohjainlaitteet

```
EscoEvObdScanner <portti> ecus [alku loppu]
```

Lähettää TesterPresent-kyselyn (`3E 00`) jokaiselle CAN-tunnukselle. Jos
tunnus ei vastaa siihen, se kysyy vielä aktiivisen istunnon (`22 F186`),
koska osa ohjainlaitteista (esim. ajoneuvon pääohjain, VCU) ei vastaa TesterPresentiin.
Listaa kaikki, jotka vastaavat. Näin selviää, mitä ohjainlaitteita autossa
on ja millä osoitteilla. Osoitteita 0x7DF ja 0x7E8–0x7EF sekä auton tiedoston
vastausosoitteita ei koskaan kysytä. Auton tiedoston ei tarvitse latautua: jos se on rikki,
alaraja on 0x600.

| Esimerkki | Mitä tekee |
|---|---|
| `ecus` | Koko alue auton tiedoston alarajasta 0x7EF:ään (e-CMP: 0x600–0x7EF, noin 500 tunnusta, noin 8 minuuttia) |
| `ecus 6A0 6BF` | e-CMP: vain 32 tunnusta, joissa VCU, BMS, moottorinohjain, ABS ja ohjaustehostin ovat. **Suositeltu ensimmäinen ajo.** |
| `ecus 7E0 7EF` | Tavalliset OBD-osoitteet |

Tuloste:

```
  ECU found: request 0x6B4 -> response 0x694
```

Ensimmäinen luku on osoite, johon kysytään, ja toinen se, josta vastaus tulee.
Uuden ECU:n voi lisätä auton tiedoston `ecus`-osioon.

Lue [riskit](#2-riskit--onko-pelkkä-lukeminen-turvallista): tämä on ainoa
komento, joka lähettää viestejä tunnuksille, joiden käyttöä ei tiedetä.

Tuloste tallentuu lokiin (`logs\ecus_<aika>.log`).

---

### `blocks` – näytä skannaussuunnitelma

```
EscoEvObdScanner - blocks
```

Listaa auton tiedoston skannauslohkot siinä järjestyksessä, jossa ne kannattaa
ajaa, sekä perustelun ja arvioidun keston. Ei tarvitse adapteria.

| Lohko | Alue | Kyselyjä | Kesto | Mitä sieltä löytyy |
|---|---|---|---|---|
| `known` | – | kaikki tunnetut | sekunteja | Tunnetut arvot (aja komennolla `known`) |
| `d8xx` | D800–D8FF | 256 | < 1 min | Akun mittaukset, kunto, lämpötilat |
| `d4xx` | D400–D4FF | 256 | < 1 min | Ajoneuvon tila: nopeus, varaustila, ulkolämpö |
| `f1xx` | F100–F1FF | 256 | < 1 min | Standarditunnisteet: versiot, osanumerot, valmistuspäivä |
| `dxxx` | D000–DFFF | 4096 | noin 10 min | Koko D-lohko: lähes kaikki Stellantiksen omat arvot |
| `full` | 0000–FFFF | 65536 | noin 2 h / ECU | Kaikki |

---

### `scan` – käy tunnisteet läpi

```
EscoEvObdScanner <portti> scan <lohko> [ecu]
EscoEvObdScanner <portti> scan <alku> <loppu> <ecu>
```

Kysyy jokaisen alueen tunnisteen (`22 xxxx`) ja tallentaa kaikki, jotka ovat
olemassa. Tämä on ohjelman pääasiallinen tehtävä.

| Esimerkki | Mitä tekee |
|---|---|
| `scan d8xx bms` | Lohko `d8xx` akunhallinnasta |
| `scan d8xx vcu` | Sama lohko ajoneuvon pääohjaimesta (VCU) |
| `scan d8xx` | Lohko lohkon ensimmäisestä ECU:sta (tässä `bms`) |
| `scan D800 D8FF bms` | Sama alue käsin annettuna |
| `scan D810 D81F bms` | Vain 16 tunnistetta |

ECU:t ovat auton tiedoston `ecus`-osiosta. e-CMP:ssä: `vcu` (ajoneuvon pääohjain, 0x6A2),
`bms` (akunhallinta, 0x6B4), `obc` (laturi, 0x590), `mcu` (sähkömoottorin ohjain, 0x6A6), `ecu6ad`
(ABS/ESP), `ecu6b5` (ohjaustehostin) ja `ecu74a` (tuulilasikamera), sekä ehdokkaat `bsi`
(keskusyksikkö), `combine` (mittaristo), `vth` (ilmastointi) ja `avas` (jalankulkijaääni),
jotka eivät ole vastanneet. `EscoEvObdScanner - blocks` ja auton tiedosto näyttävät listan.

Näin tunnisteet tulkitaan:

| ECU:n vastaus | Tulkinta | Tallennetaanko? |
|---|---|---|
| Arvo (`62 ...`) | Tunniste on olemassa | Kyllä |
| `securityAccessDenied` (0x33) | On olemassa, mutta lukittu | Kyllä |
| `conditionsNotCorrect` (0x22) | On olemassa, ei saatavilla juuri nyt | Kyllä |
| `requestOutOfRange` (0x31) | Ei ole olemassa | Ei |
| Ei vastausta | ECU ei vastannut | Ei |

Automaattiset pysäytykset:

- **50 vastaamatonta kyselyä peräkkäin** → ECU on todennäköisesti mennyt
  lepotilaan. Ohjelma pysähtyy ja tulostaa komennon, jolla voit jatkaa samasta
  kohdasta.
- **ECU hylkää koko palvelun** (`serviceNotSupported` tai `serviceNotSupportedInSession`) → ohjelma pysähtyy
  heti, koska muutkaan tunnisteet eivät vastaisi.

Paluukoodi kertoo skriptille, päättyikö skannaus: 0, kun koko alue käytiin läpi, ja 2,
kun skannaus pysähtyi ennen loppua (ECU lakkasi vastaamasta tai antoi pelkkiä virheitä,
kieltäytyi palvelusta tai adapteri ei saanut sitä osoitettua).

Tallentaa tiedoston `scan_<ecu>_<alku>-<loppu>_<aika>.jsonl`.

---

### `watch` – seuraa arvojen muutoksia

```
EscoEvObdScanner <portti> watch <ecu> <did,did,...> [väli_ms]
EscoEvObdScanner <portti> watch <ecu>:<did,...> [<ecu>:<did,...> ...] [väli_ms]
```

Lukee annettuja tunnisteita jatkuvasti ja tulostaa rivin aina, kun arvo
muuttuu. **Näin selviää, mitä tuntematon tunniste tarkoittaa:** muuta autossa
jotain ja katso, mikä liikkuu.

| Esimerkki | Mitä tekee |
|---|---|
| `watch bms D410,D815,D816` | Varaustila, jännite, virta, puolen sekunnin välein |
| `watch bms D811,D812,D813 1000` | Kolme tarkistamatonta, sekunnin välein |
| `watch vcu:D8D9,D402 bms:D815,D816` | Moottorin teho ja nopeus VCU:lta sekä akun jännite ja virta BMS:ltä vuorotellen: eri laitteiden arvot rinnakkain |

Ideoita kokeiluihin:

| Tee tämä | Mikä voisi muuttua |
|---|---|
| Kytke laturi | Virta, latausteho, latausluukun tila |
| Käynnistä lämmitys tai ilmastointi | Virta, teho |
| Aja vähän (matkustaja käyttää konetta) | Nopeus, virta, lämpötilat |
| Odota tunti latauksessa | Varaustila, energia |

Tuloste:

```
10:15:02.114   D815      367.50 V   Pack voltage
10:15:04.620 * D815      366.94 V   Pack voltage
```

`*` tarkoittaa, että arvo on muuttunut edellisestä. Lopeta painamalla
**Ctrl+C**.

Tallentaa tiedoston `watch_<ecu>_<aika>.jsonl`.

---

### `dtc` – lue vikakoodit

```
EscoEvObdScanner <portti> dtc [ecu] [--deep] [--all-ecus]
```

Lukee ohjainlaitteisiin tallennetut vikakoodit (palvelu `0x19`). **Ei
tyhjennä mitään.**

| Esimerkki | Mitä tekee |
|---|---|
| `dtc` | Auton tiedoston ECU:t paitsi ne, jotka on merkitty vastaamattomiksi (`"answered": false`) |
| `dtc --all-ecus` | Kaikki auton tiedoston ECU:t, myös vastaamattomiksi merkityt |
| `dtc bms` | Vain akunhallinta (nimellä annettu ECU luetaan aina) |
| `dtc --deep` | Lisäksi jokaisen vian tilannekuva ja lisätiedot |

Tuloste:

```
  ACTIVE   P0A7F-13  [09] testFailed, confirmedDTC  (Circuit Open)
```

- `ACTIVE` = vika päällä nyt, `stored` = vika ollut, `no fault` = testiä ei ole vielä ajettu.
- `P0A7F` on vikakoodi. P = voimalinja, C = alusta, B = kori, U = tietoliikenne.
- `-13` on vian tyyppi, ja sen nimi standardista SAE J2012-DA on rivin lopussa (esim. 13 = Circuit Open, 29 = Signal Invalid, 64 = Signal Plausibility Failure). Valmistajan omille arvoille standardi ei anna nimeä.
- `[09]` on tilatavu, ja sen perässä ovat päällä olevat bitit standardin ISO 14229-1 nimillä.
- Sovellus näyttää vian tyypin nimen *Vian tyyppi (SAE J2012-DA)* -sarakkeessa ja samat bitit *Tilabitit (ISO 14229-1)* -sarakkeessa kääntämättä sekä *Tarkoittaa*-sarakkeessa selväkielisinä: testFailed (vika päällä nyt), testFailedThisOperationCycle (vika tällä ajokerralla), pendingDTC (odottaa vahvistusta), confirmedDTC (vahvistettu vika), testNotCompletedSinceLastClear (testiä ei ole ajettu tyhjennyksen jälkeen), testFailedSinceLastClear (vika ollut tyhjennyksen jälkeen), testNotCompletedThisOperationCycle (testiä ei ajettu tällä ajokerralla) ja warningIndicatorRequested (pyytää varoitusvaloa). Tämä ohjelma ei tyhjennä vikakoodeja.

Tilannekuva (snapshot) kertoo, millaiset olosuhteet vian syntyhetkellä olivat.
e-CMP-autoissa siinä on matkamittari ja auton kello, joista Windows-sovellus
laskee vian syntyhetken (*Syntyi*-sarake). Lisätiedot (extended) sisältävät
esiintymis- ja vanhenemislaskurit. Komentorivi tallentaa molemmat raakatavuina.

Tallentaa tiedoston `dtc_<aika>.jsonl`.

---

### `scaling` – kysy ECU:lta arvojen koodaus

```
EscoEvObdScanner <portti> scaling [ecu]
```

Kysyy palvelulla `0x24`, miten kukin tunnettu tunniste koodataan: tietotyyppi,
tavumäärä, yksikkö ja muunnoskaava. Jos ECU tukee tätä, laskukaavoja ei
tarvitse arvata.

Useimmat valmistajat eivät toteuta tätä palvelua. Todennäköisin tulos on
`service 0x24 not implemented here`, jolloin ohjelma siirtyy seuraavaan ECU:hun.
Kokeileminen on silti ilmaista ja turvallista.

Tallentaa tiedoston `scaling_<aika>.jsonl` vain, jos jokin ECU antaa kuvauksen; muuten tiedostoa ei synny.

---

### `snap` – ota tilannekuva löydetyistä tunnisteista

```
EscoEvObdScanner <portti> snap [ecu] [--note "teksti"] [--rare] [--refused] [--all]
```

Lukee kerran tähän mennessä löydetyt tunnisteet (auton tiedoston `dids`- ja
`discovered`-osiot) kaikista ohjainlaitteista ja tallentaa ne yhdessä
tilamerkinnän kanssa. Kestää noin minuutin. Oletuksena pois jäävät tunnisteet,
jotka auton tiedosto merkitsee harvoin muuttuviksi (`"changes": "rarely"`), ja
tunnisteet, joiden arvon ohjainlaite kielsi skannauksessa (`kind` on `negative:…`).
`--rare` ottaa mukaan harvoin muuttuvat, `--refused` kielletyt ja `--all` molemmat.

Tarkoitus on ottaa kaksi snapia saman muutoksen molemmin puolin ja verrata
niitä `diff`-komennolla. Mikä muuttui ja kuinka paljon, kertoo mitä tunniste
tarkoittaa.

| Esimerkki | Mitä tekee |
|---|---|
| `snap --note "ennen latausta, 41 %"` | Kaikki ohjainlaitteet |
| `snap bms --note "lämmitys päällä"` | Vain akunhallinta |
| `snap --all --note "kaikki"` | Kaikki ohjainlaitteet, myös harvoin muuttuvat ja kielletyt tunnisteet |

Ota molemmat snapit **samassa tilassa** (auto READY-tilassa, laturi irti),
jotta ainoa ero on itse muutos.

Tallentaa tiedoston `snap_<aika>.jsonl`.

---

### `cars` – listaa auton tiedostot

```
EscoEvObdScanner - cars
```

Listaa `cars`-kansioiden auton tiedostot ja niiden autot. Nimeä käytetään
valitsimessa `--car`. Ei tarvitse adapteria.

### `diff` – vertaa kahta snapia

```
EscoEvObdScanner - diff <snapA.jsonl> <snapB.jsonl>
```

Listaa vain ne tunnisteet, joiden arvo muuttui. Ei tarvitse adapteria.

```
bms     0xD410  State of charge, calibrated   52E3 -> 7800   21219 -> 30720  (+9501)   [41,44 -> 60]
bms     0xD873  Discharge power limit, 10 s   0001484C -> 000148B0   84044 -> 84144  (+100)   [156,59 -> 157,38]
bms     0xD440                   86 of 240 bytes differ
```

- Lyhyet arvot näytetään heksana ja lukuna sekä erotuksena. Erotuksesta
  selviää laskurin yksikkö: jos latasit 10 kWh ja laskuri kasvoi 100, yksikkö
  on 0,1 kWh.
- Hakasulkeissa on kaavalla laskettu arvo, jos tunnisteella on kaava.
- Pitkistä arvoista (esim. kennokohtaiset jännitteet) kerrotaan, montako
  tavua muuttui.

---

### `merge` – yhdistä tulokset auton tiedostoon

```
EscoEvObdScanner - merge <tiedosto.jsonl> [tiedosto2.jsonl ...]
```

Lisää skannauksissa, snapeissa tai seurannoissa löytyneet tuntemattomat tunnisteet auton tiedoston
`discovered`-osioon. Ei tarvitse adapteria.

- Jo auton tiedostossa (`dids`) olevat ohitetaan.
- Sarjanumero (F18C), VIN (F190) ja auton tiedostossa yksilöiviksi merkityt tunnisteet
  (e-CMP:ssä D901 ja FD05) kirjataan tunnisteina ilman arvoa, koska auton tiedosto on julkinen.
- Saman tunnisteen uudet havainnot lisätään näytteiksi (`samples`).
- Uusilla tunnisteilla on nimi ja laskukaava tyhjinä. Täytä ne, kun selviää,
  mitä tunniste tarkoittaa, ja siirrä valmiit `discovered`-osiosta
  `dids`-osioon.

**Huom:**

- `merge` **muuttaa auton tiedostoa** (`cars\<auto>.json`). Aja se projektikansiossa,
  jotta muutos menee lähdetiedostoon eikä `bin`-kansion kopioon.
- Kommentit JSON-tiedostosta häviävät, kun se kirjoitetaan uudelleen.
- Ota kopio tiedostosta ennen ensimmäistä ajoa.

---

## 6. Valitsimet

Valitsimet kirjoitetaan portin ja komennon jälkeen, komennon loppuun; ennen niitä
ne eivät toimi. Taulukon viisi ensimmäistä toimivat kaikkien komentojen kanssa, muut
vain mainittujen komentojen kanssa. Valitsin, joka vaatii arvon (`--baud`, `--car`,
`--catalog`, `--out`, `--note`), on virhe ilman arvoa: ohjelma pysähtyy ilmoitukseen
eikä ohita valitsinta hiljaa.

| Valitsin | Mitä tekee | Esimerkki |
|---|---|---|
| `--baud <nopeus>` | Sarjaportin nopeus. Oletus 115200. | `--baud 38400` |
| `--trace` | Näyttää kaiken adapterin kanssa käydyn liikenteen. Hyvä vianetsintään. | `COM3 known --trace` |
| `--car <nimi>` | Valitsee auton tiedoston `cars`-kansiosta. Tarvitaan vain, jos autoja on useampi. Lista: `- cars`. | `--car stellantis-ecmp` |
| `--catalog <polku>` | Käyttää auton tiedostoa mistä tahansa. | `--catalog oma-auto.json` |
| `--out <kansio>` | Tallentaa tulokset tähän kansioon oletuskansion sijaan. | `--out D:\mittaukset` |
| `--deep` | Vain `dtc`: hakee myös tilannekuvat ja lisätiedot. | `COM3 dtc --deep` |
| `--all-ecus` | Vain `dtc`: kysyy myös ECU:ilta, jotka auton tiedosto merkitsee vastaamattomiksi (`"answered": false`). | `COM3 dtc --all-ecus` |
| `--note "<teksti>"` | Vain `snap`: tallentaa tekstin tulokseen, jotta vertailussa tietää, mitä tilannetta snap kuvaa. | `--note "ennen latausta"` |
| `--rare` | Vain `snap`: lukee myös harvoin muuttuvat tunnisteet (`"changes": "rarely"`). | `COM3 snap --rare` |
| `--refused` | Vain `snap`: lukee myös tunnisteet, joiden arvon ohjainlaite kielsi skannauksessa. | `COM3 snap --refused` |
| `--all` | Vain `snap`: sama kuin `--rare` ja `--refused` yhdessä. | `COM3 snap --all` |

---

## 7. Tulostiedostot

Tiedostot tallentuvat `Dokumentit\EscoEvObdScanner\<auto>`-kansioon, kukin tyyppi
omaan alikansioonsa (ks. [luku 0](#0-windows-sovellus)). Valitsin `--out <kansio>`
vaihtaa paikan. `merge` ja `diff` etsivät pelkällä nimellä annetut tiedostot
näistä kansioista, ja jokerimerkit (`scan_*.jsonl`) toimivat.

| Tiedosto | Komento | Sisältö |
|---|---|---|
| `scan_<ecu>_<alue>_<aika>.jsonl` | `scan` | Jokainen löytynyt tunniste ja sen raakatavut |
| `watch_<ecu>_<aika>.jsonl` | `watch` | Jokainen arvon muutos aikaleimalla |
| `dtc_<aika>.jsonl` | `dtc` | Vikakoodit |
| `scaling_<aika>.jsonl` | `scaling` | ECU:n kuvaukset arvojen koodauksesta |
| `snap_<aika>.jsonl` | `snap` | Kaikkien löydettyjen tunnisteiden arvot kerralla, muistiinpanon kanssa |
| `logs\<komento>_<aika>.log` | kaikki adapteria käyttävät | Koko ruudun tuloste, myös `--trace`-liikenne. Tulosteita ei tarvitse kopioida käsin. |

Tiedostot ovat JSON Lines -muotoa: yksi JSON-olio per rivi. Ne voi avata
tekstieditorilla.

> **Yksityisyys:** tiedostot voivat sisältää auton VIN-koodin ja
> ohjainlaitteiden sarjanumerot. `.gitignore` estää niiden päätymisen
> GitHubiin. Älä liitä niitä julkisiin keskusteluihin sellaisenaan.

---

## 8. Vianetsintä

| Oire | Todennäköinen syy | Korjaus |
|---|---|---|
| `Could not open COM3` | Väärä portti, tai jokin muu ohjelma käyttää porttia | Tarkista portti Laitehallinnasta. Sulje muut OBD-sovellukset. |
| `The adapter does not answer` | Väärä nopeus | Lisää `--baud 38400`. Kokeile myös `9600`. |
| Kaikki rivit `no-data` | Auto ei ole READY-tilassa | Auto READY-tilaan. |
| Kennojännitteet eivät näy, muut arvot näkyvät | Adapteri ei tue pitkiä vastauksia tai vuonohjauksen komentoja (`ATFCSH` saa vastauksen `?`) | Kokeile toista adapteria. |
| Kaikki rivit `adapter-error` | Adapteri ei tue tarvittavia komentoja (halpa kopio) | Katso `--trace`-tulosteesta, mihin komentoon adapteri vastaa `?`. |
| Osa arvoista `no-data`, osa toimii | Osa tunnisteista toimii vain tietyissä tiloissa, esim. ei latauksen aikana | Normaalia. |
| `Could not read catalogue` / `No car files found` | Auton tiedosto puuttuu tai on rikki | Aja projektikansiossa tai valitse: `--car` tai `--catalog`. |
| Skannaus pysähtyy: *No answer to 50 queries* | Auto meni lepotilaan | Auto READY-tilaan ja jatka tulostetulla komennolla. |

`--trace` on paras apu: se näyttää jokaisen komennon ja adapterin vastauksen
sellaisenaan.

---

## 9. Auton tiedosto

Ohjelma ei tunne yhtään autoa itse. Kaikki autokohtainen tulee auton
tiedostosta `cars`-kansiossa: ohjainlaitteiden osoitteet, tunnisteet ja kaavat,
mistä mittariston kukin arvo luetaan, akun kennomäärä ja mittarien asteikot.

- **Sovelluksessa** auto valitaan Yhteys-välilehdeltä. Vaihda autoa, kun yhteys
  on katkaistu. *Tuo auton tiedosto…* kopioi muualta saadun tiedoston omiin
  autoihin.
- **Komentorivillä** yksi auto valitaan automaattisesti; useammasta valitaan
  `--car <nimi>`. `EscoEvObdScanner - cars` listaa autot.
- **Uusi auto** tarvitsee uuden tiedoston, ei koodia. Rakenne ja mittariston
  arvojen nimet on kuvattu READMEn kohdassa *Adding a car*.
- **Päivitykset:** sovellus käyttää auton tiedostosta omaa työkopiota, koska
  skannauslöydöt lisätään siihen. Kun uudempi ohjelmaversio tuo uudemman
  auton tiedoston, työkopio päivittyy itsestään: ilman omia löytöjä se korvataan,
  ja omat löydöt siirretään uuden tiedoston päälle. Molemmissa tapauksissa vanha
  kopio jää talteen kansioon `cars\backup`. Työkopio, joka ei lataudu auton tiedostona
  (esim. rikkoutunut käsin muokatessa), korvataan samoin alkuperäisellä, ja
  rikkinäinen kopio jää talteen. Loki kertoo, kun näin tapahtuu. Itse tuotua
  auton tiedostoa ei korvata päivityksessä.

## 10. Sanasto

| Termi | Selitys |
|---|---|
| **ECU** | Ohjainlaite, esim. akunhallinta (BMS) tai ajoneuvon pääohjain (VCU) |
| **DID** | Data Identifier: 16-bittinen tunniste, jolla ECU:lta pyydetään yksi tieto. Esim. `F190` = VIN. |
| **UDS** | Unified Diagnostic Services (ISO 14229): diagnostiikkaprotokolla, jota ohjelma käyttää |
| **NRC** | Negative Response Code: ECU:n syy kieltäytyä |
| **DTC** | Diagnostic Trouble Code: vikakoodi |
| **ELM327 / STN** | Adapteripiirit, jotka kääntävät tietokoneen komennot CAN-väylälle |
| **READY-tila** | Sähköauton ”käynnissä”-tila: ajovalmis, ajoakku kytketty |
| **Auton tiedosto** | `cars\<auto>.json`: auton tiedot, mittariston arvojen paikat, ECU:t, tunnetut ja löydetyt tunnisteet, laskukaavat ja skannauslohkot |

Yleisimmät NRC:t:

| Koodi | Nimi | Tarkoittaa |
|---|---|---|
| `0x11` | serviceNotSupported | ECU ei tue palvelua ollenkaan |
| `0x22` | conditionsNotCorrect | Ei nyt – olosuhteet väärät |
| `0x31` | requestOutOfRange | Tunnistetta ei ole |
| `0x33` | securityAccessDenied | Tunniste on lukittu. **Jätä rauhaan.** |
| `0x78` | responsePending | ECU tarvitsee lisäaikaa (ohjelma odottaa automaattisesti) |
| `0x7F` | serviceNotSupportedInSession | Palvelu ei ole käytössä tässä istunnossa |

### Laskukaavat auton tiedostossa

Tavut numeroidaan vastauksen alusta pituustavu mukaan lukien, joten
varsinainen data alkaa kohdasta `B4`.

| Kaava | Tarkoittaa |
|---|---|
| `B4` | Yksi tavu, etumerkitön (0–255) |
| `[B4:B5]` | Tavut 4–5 yhtenä lukuna (0–65535) |
| `SB4`, `S[B4:B5]` | Samat etumerkillisinä: miinusarvot mahdollisia, esim. pakkaslukemat ja akun latausvirta |
| `+ - * / ( )` | Laskutoimitukset |

Esimerkki: `[B4:B5]/16` ja raakatavut `16 F8` → 0x16F8 = 5880 → 5880 / 16 =
367,5 V.
